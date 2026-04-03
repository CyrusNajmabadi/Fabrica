# Research: Persistent Data Structures & Immutability for Concurrent Systems

*Conducted 2026-04-03. Focus on shared immutable state and lifecycle management.*

---

## Why Immutability Matters for Concurrency

Immutable data sits in **Shared** cache state across all cores — zero invalidation traffic. Any core
can read without coordination. The only synchronization cost is managing **which version** each
consumer reads and **when old versions can be freed**.

---

## Persistent Data Structure Fundamentals

### Path Copying & Structural Sharing

To change an immutable tree: allocate only nodes on the path from root to the updated leaf.
Unchanged subtrees are shared by pointer. Memory per update: O(log N) for balanced trees, vs O(N)
for full copy.

### HAMT (Hash Array Mapped Trie)

- Bagwell's design: Hash bits select children in a trie. Wide branching factor (32) minimizes depth.
- Updates path-copy only the spine. Lookup is O(log32 N) ≈ O(1) for practical sizes.
- **CHAMP** (Steindorfer & Vinju, OOPSLA 2015): Optimized HAMT layout for JVM, better cache
  behavior and smaller footprint.

**Source**: Bagwell, *Ideal Hash Trees* [PDF](https://lampwww.epfl.ch/papers/idealhashtrees.pdf)
**Source**: Steindorfer, CHAMP [PDF](https://michael.steindorfer.name/publications/oopsla15.pdf)

### Clojure's PersistentVector

- Wide branching factor 32. Trie navigation by 5-bit index slices.
- Near-constant-time indexed access. Efficient append via tail optimization.
- RRB-tree extension for efficient concatenation/splits.

**Source**: Hypirion series (hypirion.com/musings/understanding-persistent-vector-pt-1)

---

## MVCC: The Database Analog

### The Parallel to Sim/Render

MVCC keeps multiple row versions; each transaction sees a snapshot. This parallels Fabrica:
- Renderer reads **tick N** while simulation writes **tick N+1**.
- Old versions stay alive until no reader needs them.
- Cleanup (VACUUM) reclaims old version storage.

### PostgreSQL

- Tuples carry `xmin`/`xmax` for visibility.
- VACUUM reclaims dead tuples, manages transaction ID wraparound.
- Background process — not blocking the hot path.

### LMDB

- COW B-tree: Pages are never overwritten in place.
- Readers see consistent snapshot via mmap views.
- Only the last committed root matters for new transactions.

---

## Reference Counting for Persistent Structures

### Why RC Works for Trees

- Persistent trees/DAGs built from immutable nodes have **no cycles** in the ownership graph.
- RC is correct and complete without tracing GC.
- This is a key property for Fabrica's functional world state.

### The Cost Problem

- Per-pointer atomic RC from multiple cores: 32-42% of runtime in Swift workloads (pre-BRC).
- Solution: Deferred/biased RC (see deferred-reference-counting.md).

### Immer (C++ Persistent Data Structures)

- Default: Thread-safe refcount + thread-local free lists + global lock-free overflow.
- Optional: Boehm GC with `no_refcount_policy`.
- **Transients**: Mutable batch API reduces allocations for bulk updates (like Clojure transients).
- ICFP 2017 paper on RRB vectors in systems languages.

**Source**: immer docs (sinusoid.es/immer), ICFP paper
[PDF](https://sinusoid.es/misc/immer/immer-icfp17.pdf)

---

## Epoch-Based Cleanup for Persistent Structures

Instead of per-node refcounting, track which **epoch** (tick/frame) each version was created in.
When all consumers have advanced past that epoch, old version's unique nodes can be freed.

**Advantages over per-node RC**:
- Zero per-pointer atomics during normal operation.
- Bulk free at epoch boundaries.
- Natural fit for tick-based simulation.

**Disadvantages**:
- Memory held longer (until epoch advances, not immediately at refcount zero).
- Stalled consumers delay cleanup.

**Hybrid approach for Fabrica**: Use per-thread SPSC logs to record "I'm done with version X" at
job completion. Cleanup job processes logs and frees epoch-exclusive nodes.

---

## .NET Immutable Collections

### System.Collections.Immutable

- Immutable lists, dictionaries, sorted sets, etc.
- Tree-backed types allocate O(log N) nodes per update — GC churn in hot loops.
- `FrozenDictionary` / `FrozenSet` (.NET 8+): Optimized for read-heavy fixed key sets.

**Performance reality**: Large gaps vs mutable collections for heavy mutation. Microsoft's
"freezable binary tree" optimizations help but don't eliminate the overhead.

**Source**: Ayende, *Immutable Collections Performance* (ayende.com)
**Source**: Microsoft Premier Developer blog on mutable performance for immutable collections

### Practical Consideration

For Fabrica's world state, the question is whether to use `System.Collections.Immutable` (managed,
GC-friendly) or build custom persistent structures with unmanaged backing (arena/pool allocated
nodes, explicit lifecycle). The latter avoids GC entirely but requires careful lifetime management.

---

## The "Functional Core, Imperative Shell" Pattern

- **Core**: Pure rules for "given world + inputs → next world" (or delta).
- **Shell**: Runs render API, audio, input, network, asset I/O. Swaps which version handle is
  current.
- Maps to Fabrica: Deterministic simulation core produces immutable world snapshots. Pipeline
  shell manages version lifecycle and consumer handoff.

**Source**: Bernhardt, *Functional Core, Imperative Shell* (destroyallsoftware.com)
