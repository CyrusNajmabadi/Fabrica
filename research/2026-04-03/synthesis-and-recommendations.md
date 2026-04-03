# Research Synthesis & Recommendations for Fabrica

*Compiled 2026-04-03 from seven parallel research investigations.*

---

## The Core Principle

Every high-performance engine, database, and concurrent system converges on:

> **Minimize the number of cache lines that are written to by more than one core.**

The specific technique varies, but the goal is always the same: keep each core's writes local,
and batch cross-core coordination to well-defined boundaries.

---

## What Real Engines Actually Do

### Scheduling Layer

- **Job DAGs with atomic counters** for completion (Naughty Dog, Bungie, Unity, Unreal).
- One `Interlocked.Decrement` per job completion is acceptable — the ratio of useful work to
  one atomic decrement is what makes this tolerable.
- **Work stealing** (Chase-Lev deque) for load balancing idle workers.

### Data Layer (Where the Real Wins Are)

- **Per-job disjoint data slices**: Each job operates on a range of a contiguous array. No
  sharing during execution. SoA layout for prefetcher-friendly access.
- **One-way pipelines**: Sim produces snapshot, render consumes. No concurrent mutation.
- **Deferred structural changes**: Entity creation/destruction via command buffers flushed at
  phase boundaries.

### Memory Layer

- **Per-thread bump allocators** (Unity `Allocator.Temp`): Reset each frame. Zero bookkeeping.
- **Fixed-size free lists** (Jolt `FixedSizeFreeList`): Simple, bounded.
- **Thread-local free lists with global overflow** (Immer): Local fast path, rare global fallback.

---

## Recommended Architecture for Fabrica

### 1. Job Pool: Option A (Treiber Stack)

Use the simple shared Treiber stack (`JobPool<TJob, TAllocator>`). It's what Jolt and similar
engines use for job object reuse. The CAS contention is negligible in the coordinator-rents /
workers-return pattern. The WorkStealingDeque-based Option B solves a problem that doesn't exist
for pooling.

### 2. Per-Thread SPSC Log Buffers for Deferred Operations

Each worker thread has a dedicated SPSC ring buffer:

```
Worker execution:
  - Read shared immutable data (Shared cache state — free)
  - Write to thread-local output buffers (no contention)
  - Push "release object X" into thread-local SPSC log (one release store per batch)

Between ticks:
  - Cleanup job drains all SPSC logs
  - Processes refcount changes NON-ATOMICALLY on one thread
  - Returns freed objects to per-thread pools
```

This gives:
- **Zero atomic refcount operations on worker threads**
- **Zero cross-core cache line bouncing during job execution**
- **Deterministic cleanup ordering**

### 3. Job DAG with CountdownEvent for Fork-Join

Standard pattern used by every engine. Single atomic decrement per job completion is acceptable.
For Fabrica's thread count (4-16), the per-thread-flag alternative adds complexity for minimal
real-world gain.

### 4. Epoch/Tick-Based Reclamation

Tie node lifetimes to ticks, not individual refcount operations:

```
Tick T:   Workers execute, push "done with version X" to SPSC logs
Tick T+1: Cleanup job processes tick-T logs, decrements non-atomically
          Objects hitting zero → returned to per-thread pools
Tick T+2: When ALL consumers past tick T, bulk-free tick-T-exclusive nodes
```

This is PostgreSQL's VACUUM / crossbeam-epoch / LMDB's COW, just at tick granularity.

### 5. Per-Thread Pools (Simple Stack<T>)

For recycling freed nodes back to the thread that will reuse them. No work stealing needed
for the pool — stealing is for the execution deque (PR 4), not the object pool.

### 6. Zero-GC Hot Path

- Jobs as structs with generic constraints (`where TJob : struct, IAllocator<TJob>`).
- Pre-allocated arrays for job data.
- No LINQ, no lambdas, no string formatting in tick loop.
- `ArrayPool<T>` for temporary buffers.
- Validate with `BenchmarkDotNet MemoryDiagnoser` and `dotnet-counters`.

---

## The Full Data Flow

```
┌─────────────────────────────────────────────────────────────────┐
│  TICK LOOP                                                      │
│                                                                 │
│  1. Coordinator rents N jobs from pool (Treiber stack CAS)      │
│  2. Configures jobs with disjoint data slices                   │
│  3. Pushes jobs to execution deque (WorkStealingDeque)          │
│  4. Workers pop/steal jobs, execute on local data               │
│     - Reads: shared immutable state (Shared cache lines)        │
│     - Writes: thread-local output + SPSC log (no contention)   │
│  5. Each worker decrements CountdownEvent on completion         │
│  6. Coordinator waits (stealing other work while waiting)       │
│  7. All done → coordinator kicks cleanup job                    │
│                                                                 │
│  CLEANUP JOB (runs on one thread):                              │
│  8. Drains all SPSC logs (N sequential reads)                   │
│  9. Processes refcount changes non-atomically                   │
│  10. Frees dead objects → per-thread pool stacks                │
│  11. Advances epoch marker                                      │
│                                                                 │
│  NEXT TICK                                                      │
│  12. Pools are warm → zero allocations in steady state          │
└─────────────────────────────────────────────────────────────────┘
```

---

## Validated Anti-Patterns (Don't Do These)

1. **Work-stealing deque as pool backing store** — Confirmed unusual/unnecessary. The steal CAS
   solves load balancing, not allocation.
2. **Per-pointer atomic refcounts from multiple cores** — 32-42% of runtime in Swift workloads
   pre-BRC. Use deferred/biased/epoch-based RC instead.
3. **Assuming Gen0 is free** — Gen0 collections are STW foreground events. Zero allocations in
   the tick loop is the correct target.
4. **Pooling tiny objects that could be structs** — Structs in pre-allocated arrays beat class
   pooling for small fixed-layout data.
5. **Global MPSC queue for logging** — Per-thread SPSC is strictly better (no producer CAS,
   no cross-producer contention).

---

## Research Documents (This Directory)

- `deferred-reference-counting.md` — DRC, Biased RC, EBR, Hazard Pointers, percpu_ref
- `cache-friendly-concurrency.md` — MESI, false sharing, atomics cost, Disruptor, batching
- `spsc-queues-and-per-thread-logging.md` — SPSC mechanics, batching, GC logging patterns
- `persistent-data-structures-and-immutability.md` — HAMT, MVCC, Immer, .NET immutables
- `gc-free-dotnet-patterns.md` — Zero-alloc patterns, profiling, real-world systems
- `job-dependency-systems.md` — Unity, Unreal, TBB, Taskflow, CountdownEvent tradeoffs
- `real-engine-architectures.md` — Naughty Dog, id Tech, Bungie, Bevy, EnTT, DOD
- `engine-job-pooling-survey.md` — How engines pool job objects (spoiler: simply)
- `../2026-04-02/dynamic-resource-allocation-and-job-systems.md` — Prior research on unified pools
