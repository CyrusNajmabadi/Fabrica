# Research: Dynamic Resource Allocation & Unified Job Systems

*Conducted 2026-04-02 during unified job pool design.*

---

## Context

Researched industry best practices for dynamically allocating CPU cores/threads, work-shedding, and
unified job systems. Key constraints for Fabrica:

- **Simulation must be deterministic** (time dilation allowed, work-shedding per tick is not)
- **Rendering can shed work** (quality scaling)
- **Latency is prioritized** over throughput

---

## Key Findings

### 1. Unified Thread Pools (One Thread Per Core)

Modern engines converge on **one thread per physical core**, pinned, with all subsystems sharing the
pool:

- **Naughty Dog (GDC 2015)**: Fiber-based job system. 160 fibers with two stack sizes. Workers
  pinned to cores. Jobs yield via `wait_for_counter` — fiber sleeps, another runs. No OS thread
  blocking.
- **id Tech 7 (DOOM Eternal)**: No classic "main thread." Everything is jobs. Heavy GPU-side work
  with job-based CPU orchestration.
- **Unity DOTS**: `IJob` structs scheduled with dependency handles. Workers steal from each other.
  Burst compiler eliminates managed overhead.

### 2. Work Stealing (Chase-Lev Deque)

The standard scheduling primitive across engines and runtimes:

- Each worker owns a **deque** (double-ended queue)
- Owner pushes/pops from the **bottom** (LIFO, no CAS in common case)
- Thieves steal from the **top** (FIFO, CAS required)
- Contention only on the last item (owner vs thief race)

Used in: Cilk, Intel TBB, Go runtime, Rust Tokio, Java ForkJoinPool, .NET ThreadPool.

### 3. Coordinator-as-Worker (Option B — chosen for Fabrica)

Instead of dedicated coordinator threads that block while waiting:

- Coordinators run on worker threads (Thread 0 = sim coordinator, Thread 1 = render coordinator)
- While waiting for sub-jobs, coordinators **steal and execute** pool jobs
- Zero wasted cores
- Coordinator ownership semantics preserved (always same physical thread)

### 4. Deterministic Simulation Under Parallelism

- **Factorio approach**: Single-threaded simulation core. Parallelism only where order doesn't
  matter (independent chunks). Determinism by construction.
- **Age of Empires IV ("The MAW")**: Machine-checked dependency validation. Parallelism where
  proven safe.
- **Fabrica approach**: Fork-join per tick. All workers process disjoint slices of the world.
  Determinism from non-overlapping writes + deterministic scheduling order.

### 5. CountdownEvent for Fork-Join

.NET's `CountdownEvent` is sufficient for fork-join synchronization. Custom `JobCounter` type was
deemed unnecessary. Each worker decrements on completion; coordinator polls while stealing work.

---

## Sources

- Gyrling, *Parallelizing the Naughty Dog Engine Using Fibers*, GDC 2015
- Genova, *Multithreading the Entire Destiny Engine*, GDC 2015
- Acton, *Data-Oriented Design and C++*, CppCon 2014
- Chase & Lev, *Dynamic Circular Work-Stealing Deque*, SPAA 2005
- Frigo et al., *The Implementation of the Cilk-5 Multithreaded Language*, PLDI 1998
