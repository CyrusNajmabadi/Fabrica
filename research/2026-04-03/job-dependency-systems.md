# Research: Job Dependency DAG Systems

*Conducted 2026-04-03. How engines implement "fan out N jobs, then run cleanup."*

---

## The Pattern

A coordinator fans out N worker jobs to run in parallel. Each worker processes a slice of data.
A cleanup/collection job depends on all N workers and runs only after they all complete. This is
the fundamental fork-join-then-collect pattern.

---

## Engine Implementations

### Unity: JobHandle Dependencies

- `Schedule(dependsOn)` links a job into a dependency chain.
- `JobHandle.CombineDependencies()` merges multiple handles into a single join point.
- Internally: opaque completion fences. Workers steal from each other.
- **Limitation**: Deep chains add scheduling latency.

**Source**: Unity Manual, *Job dependencies* (docs.unity3d.com)

### Unreal: Task Graph

- `FBaseGraphTask` carries `NumberOfPrerequisitesOutstanding` (atomic counter).
- Each predecessor calls `PrerequisitesComplete` on successors.
- When count reaches zero → task queued for execution.
- Constructor initializes counter with +1 as "hold" until prerequisites are wired.
- **O(1) per edge completion** (atomic decrement + compare).

**Source**: Epic API docs, community source analysis

### Intel TBB / oneTBB: Flow Graph

- `continue_node`: Counts `continue_msg` tokens from predecessors. Runs body when count matches.
- Nodes spawn TBB tasks into the work-stealing pool.
- `wait_for_all()` on graph coordinates completion.
- Hot fine-grained graphs pay per-node task spawn overhead.

**Source**: oneTBB docs, *Dependence Graph* (oneapi-src.github.io/oneTBB)

### Taskflow (C++)

- Explicit `precede` / dependency edges in DAG builder.
- Executor: Work-stealing scheduler with per-thread task cache.
- `wait_for_all()` with idle list + condition variable for thread parking.

**Source**: Taskflow IPDPS 2019 paper [PDF](https://taskflow.github.io/papers/ipdps19.pdf)

### Apple GCD

- `dispatch_group_enter` / `dispatch_group_leave` balance like a refcount.
- `dispatch_group_notify` schedules a block when group hits zero.
- Implementation: Mach semaphore for blocking wait, not spin-on-atomic.

**Source**: Apple Developer docs, Levin *GCD Internals*

---

## .NET: ContinueWhenAll

- `TaskFactory.ContinueWhenAll` internally uses `CompleteOnCountdownPromise`.
- `_count` initialized to `tasks.Length`.
- Each antecedent's completion action does `Interlocked.Decrement(ref _count)`.
- When zero → `TrySetResult` on the continuation task.
- Continuation scheduled through `TaskScheduler` (thread pool by default).

**Source**: dotnet/runtime TaskFactory.cs, `CompleteOnCountdownPromise`

---

## The CountdownEvent / Atomic Counter Pattern

### Standard Approach

- Initialize count = N.
- Each worker: `Interlocked.Decrement(ref count)` on completion.
- Exactly one thread observes zero → signals/runs dependent work.

### Cache Coherence Cost

- **One hot cache line** for the counter.
- N workers decrementing = N MESI invalidations.
- Mitigations: Padding the counter to its own cache line. Accepting the cost (it's O(1) per job,
  not per data access).

### Alternatives to Shared Atomic Counter

| Approach | Worker Cost | Join Cost | Notes |
|----------|-------------|-----------|-------|
| Shared `CountdownEvent` | 1 atomic decrement | O(1) test for zero | Standard. One hot line. |
| Per-thread done flags | 1 plain store (own line) | O(N) reads | No cross-worker contention. |
| Tree reduction | 1 atomic per tree level | O(log N) | Hierarchical; complex. |
| SPSC queue per worker | 1 release store | O(N) drains | Amortized if batched. |

### Per-Thread Done Flags (Best for Fabrica?)

```
Worker i: done[i * CACHE_LINE_SIZE] = true;  // plain store, own cache line
Cleanup job: for each i, read done[i * CACHE_LINE_SIZE]  // O(N) reads in Shared state
```

- **Zero cross-worker contention** on writes (each flag on its own line).
- Cleanup job's reads may hit L1/L2 if done flags recently written (cache-to-cache transfer).
- **Simpler than CountdownEvent** for the "all workers done, then run cleanup" case.

### Recommendation

For Fabrica's fork-join-then-cleanup pattern, **`CountdownEvent`** is pragmatic. The single atomic
decrement per job completion is negligible compared to the useful work each job does. The per-thread
flag approach is theoretically better but adds complexity for minimal real-world gain when job
count is 4-16.

---

## Structured Parallelism

### Cilk

- Fork/join as language primitives. Work-stealing on worker deques.
- Theoretical bounds: T_P ≤ T_1/P + O(T_∞).

### Java (Project Loom)

- `StructuredTaskScope`: Fork subtasks, join with cancellation policy, scoped to block.
- Virtual threads are cheap (~1KB stack initially).

### Swift

- `withTaskGroup` / `TaskGroup`: Child tasks run in scope. Group waits for children.
- Cooperative executor handles scheduling.

---

## Sources

- Unity: docs.unity3d.com/Manual/JobSystemJobDependencies
- Unreal: dev.epicgames.com FTaskGraphInterface
- TBB: oneapi-src.github.io/oneTBB
- Taskflow: taskflow.github.io/papers/ipdps19.pdf
- Cilk: cilk.mit.edu/runtime
- .NET: dotnet/runtime TaskFactory.cs
- Shavit & Zemach, *Scalable Concurrent Counting* (dl.acm.org)
