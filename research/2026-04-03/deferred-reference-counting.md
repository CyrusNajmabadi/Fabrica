# Research: Deferred Reference Counting & Memory Reclamation

*Conducted 2026-04-03. Deep investigation into avoiding cross-core atomic refcount operations.*

---

## The Problem

Every `Interlocked.Increment` / `Interlocked.Decrement` on a shared refcount forces MESI cache line
invalidation across all cores. If 8 workers decrement the same object's refcount, that cache line
bounces 8 times (~40-80ns per bounce on modern hardware). This serializes what should be parallel.

---

## Techniques Surveyed

### 1. Deutsch & Bobrow Deferred RC (1976)

- **Original concept**: Log pointer events to a transaction file instead of updating refcounts
  immediately. Reconciliation algorithm processes logs in batch.
- **Zero Count Table (ZCT)**: Objects with heap-refcount zero. Only reclaimable if also not
  referenced by stack/registers (checked via Variable Reference Table scan).
- **Key insight**: Refcount work scales with pointer transactions, not live heap size.

**Source**: Deutsch & Bobrow, *An Efficient, Incremental, Automatic Garbage Collector*, CACM 1976.
[PDF](https://people.cs.umass.edu/~emery/classes/cmpsci691s-fall2004/papers/p522-deutsch.pdf)

### 2. Biased Reference Counting (Swift ARC, PACT 2018)

- **Header layout**: 64-bit word split into biased half (owner TID + 14-bit counter) and shared
  half (14-bit shared counter + flags).
- **Fast path**: Owner thread does **non-atomic** inc/dec on biased counter.
- **Slow path**: Non-owners use **atomic CAS** on the shared half only.
- **Deallocation**: Only when biased + shared = zero after merge.
- **Results**: >99% of RC ops touch private objects in client workloads. 22.5% average runtime
  reduction. RC was 32% of total execution time before optimization.

**Source**: Choi, Shull, Torrellas, *Biased Reference Counting*, PACT 2018.
[PDF](https://iacoma.cs.uiuc.edu/iacoma-papers/pact18.pdf)

### 3. Epoch-Based Reclamation (EBR)

- Threads **pin** an epoch while holding references. Objects retired to thread-local garbage bags
  tagged with epoch. When all threads advance past an epoch, objects from that epoch are freed.
- **Used in**: crossbeam-epoch (Rust), RCU (Linux kernel).
- **Weakness**: Stalled thread prevents epoch advancement → unbounded garbage accumulation.
- **Maps to Fabrica ticks**: If slowest consumer has advanced past tick T, all tick-T-exclusive
  nodes can be bulk-freed.

**Source**: crossbeam-epoch (github.com/crossbeam-rs/crossbeam)
**Source**: McKenney, *A Tour Through TREE_RCU's Grace-Period Memory Ordering* (kernel.org)

### 4. Hazard Pointers (Maged Michael, 2004)

- Each thread has K hazard slots. Before using a pointer, publish it in a slot. Reclaimers scan
  all threads' slots before freeing.
- **Bounded memory**: constant × threads. Better worst-case than EBR.
- **Higher overhead**: O(threads) scan per retire batch.

**Source**: Michael, *Hazard Pointers: Safe Memory Reclamation for Lock-Free Objects*, IEEE TPDS 2004.
[PDF](https://www.cs.otago.ac.nz/cosc440/readings/hazard-pointers.pdf)

### 5. QSBR (Quiescent-State-Based Reclamation)

- Threads declare **quiescence** (no held pointers) rather than protection.
- Cheaper than EBR in tight loops where quiescent points are natural.
- **Used in**: liburcu, DPDK `rte_rcu_qsbr`.
- **Constraint**: Application must know quiescent states. Wrong placement → use-after-free.

**Source**: liburcu.org, DPDK RCU Programmer's Guide (doc.dpdk.org)

### 6. Linux percpu_ref

- **Steady state**: Each CPU adjusts its own counter — no cross-cache-line atomicity.
- **Teardown**: `percpu_ref_kill()` switches to atomic mode so zero-detection is well-defined.
- **Transition** uses RCU-style safety (`synchronize_rcu()`).
- **Exactly the pattern**: Avoid cross-core atomics in steady state; pay for coordination only
  during the rare "object is dying" transition.

**Source**: Corbet, *Per-CPU reference counts*, LWN 2015. (lwn.net/Articles/557478)

---

## Concurrent Deferred RC (Modern Academic Work)

- Anderson et al., *Concurrent Deferred Reference Counting with Constant-Time Overhead*, PLDI 2021.
  [ACM](https://dl.acm.org/doi/10.1145/3453483.3454060),
  [Code](https://github.com/cmuparlay/concurrent_deferred_rc)

---

## Recommendation for Fabrica

**Per-thread SPSC log buffers + single-threaded cleanup job** is the best fit:

1. Workers push "release object X" into their thread-local SPSC ring (one release store per batch).
2. Cleanup job drains all rings, processes refcount changes **non-atomically on one thread**.
3. Objects hitting zero are returned to per-thread pools.
4. Epoch/tick alignment means cleanup only processes items from completed ticks.

This gives:
- **Zero atomic refcount operations on worker threads**
- **Zero cross-core cache line bouncing during job execution**
- **Deterministic cleanup ordering** (single-threaded collector)
- **Bounded memory** (cleanup runs every tick boundary)
