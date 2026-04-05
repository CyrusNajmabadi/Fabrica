# Job System, Tagged Handles, and Parallel Coordinator Merge

Supersedes `jobs-coordinators-pipeline.md`. This plan was developed through extensive discussion
covering job DAG data flow, handle encoding for cross-thread references, and parallel merge.

## Design Decisions (Resolved in Discussion)

- **Job dependencies**: Atomic counters. Each job has a counter initialized to its dependency
  count. Completing a dependency does `Interlocked.Decrement`; the worker that hits zero enqueues
  the dependent job. No central scheduler thread.
- **Job lifetimes**: Coordinator batch-owns the entire DAG. There is always a single `RootJob`
  that the coordinator enqueues; the root job decides which sub-jobs to create and enqueue. The
  coordinator waits for the root job's terminal counter, then sweeps the whole DAG returning
  everything to the pool.
- **Job granularity**: "Slightly chunky" — hundreds to thousands of operations per job. Counter
  overhead (~10-20ns) is negligible relative to useful work.
- **Job pooling**: Treiber stack. With chunky jobs, pool contention is not a hot path.
- **Job dispatch**: `WorkStealingDeque<T>` (already implemented in
  `src/Fabrica.Core/Collections/WorkStealingDeque.cs`).
- **Fibers**: Rejected. Too specialized, poor portability, loss of control/clarity.
- **Data flow**: Jobs create nodes in thread-local buffers (sideways flow). Job-to-job
  intermediate data flows through typed output buffers on job objects (by reference, not copied).
  The coordinator merges TLB contents into the global arena at the join point.
- **Tagged handles**: 1 bit global/local + 7 bits thread ID + 24 bits local index. Global handles
  have bit 31 = 0 (31 bits = 2B entries). Local handles encode which thread's TLB the node lives
  in (128 threads, 16M local nodes per thread per work phase), enabling cross-thread references
  within job DAGs.
- **Per-type TLBs**: Each worker thread has one TLB per node type. This enables the coordinator
  to process types in parallel during merge.
- **Parallel coordinator merge**: One merge-worker per node type, all running concurrently. Safe
  because each type's `NodeStore`/`RefCountTable` is independent.

---

## Part 1: Job System

### 1A. JobCounter (`Fabrica.Core/Threading/JobCounter.cs`)

Atomic dependency counter with cache-line padding.

```csharp
internal struct JobCounter
{
    private int _remaining;

    public JobCounter(int count) => _remaining = count;
    public bool IsComplete => Volatile.Read(ref _remaining) == 0;

    // Returns true if this decrement brought the count to zero.
    public bool Decrement()
        => Interlocked.Decrement(ref _remaining) == 0;
}
```

When a job completes, it decrements all its dependents' counters. If any hit zero, it pushes them
to the work-stealing deque. The worker that completes the last dependency enqueues the join job —
distributed scheduling, no central thread.

### 1B. Job Type

Abstract base class with strongly-typed subclasses for real jobs. The base class owns the
scheduling/pooling machinery; subclasses own the strongly-typed input/output buffers that flow
between jobs.

```csharp
internal abstract class Job
{
    // Scheduling
    internal JobCounter Counter;           // how many dependencies must complete before this runs
    internal Job[]? Dependents;            // jobs to decrement when this job completes

    // Pooling (Treiber stack linkage)
    internal Job? PoolNext;

    // Thread context (set by the scheduler before Execute)
    internal WorkerContext? WorkerContext;  // gives access to this thread's TLBs, deque, etc.

    internal abstract void Execute();
    internal abstract void Reset();        // clear state for pool reuse
}
```

Subclasses are strongly typed with specialized buffers:

```csharp
internal sealed class ComputePositionsJob : Job
{
    public ReadOnlySpan<EntityId> InputEntities;  // set by parent job
    public Span<Vector3> OutputPositions;          // read by dependent job

    internal override void Execute() { ... }
}
```

The `WorkerContext` passed to each job provides access to thread-specific resources: the TLBs for
each node type (for creating new world-state nodes), the worker's deque (for enqueuing sub-jobs),
and the thread ID (for handle encoding). This is critical for `TryStealAndExecute` — when a worker
steals a job, the scheduler sets the stolen job's `WorkerContext` to the stealing thread's context
before calling `Execute()`.

### 1C. JobPool (Treiber Stack)

Reuse PR #92 (`feature/job-pool-option-a`) as starting point. Lock-free shared stack. `Rent()` /
`Return()` with single CAS per operation. Uses the intrusive `PoolNext` field on the `Job` base
class.

### 1D. JobScheduler

Owns N worker threads (one per core, pinned). Each worker has a `WorkStealingDeque<Job>` for
dispatch.

Worker loop:

1. Pop from own deque (LIFO — cache-hot)
2. If empty, steal from random peer (FIFO — large tasks)
3. Execute job: run work, then for each dependent counter call `Decrement()`; if zero, push
   dependent job to own deque
4. If no work: spin briefly, then park on event

Coordinator participation: while waiting for a top-level counter, the coordinator thread steals
and executes pool jobs instead of blocking.

### 1E. DAG Construction Example

The coordinator always enqueues a single `RootJob`. The root job creates the sub-job DAG:

```
Coordinator:
  rootJob = pool.Rent<SimTickRootJob>();
  rootJob.Counter = new JobCounter(1);  // terminal counter — coordinator waits on this
  rootJob.SnapshotData = currentSnapshot;

  deque.Push(rootJob);

  // Wait for rootJob to complete (including all sub-jobs) by stealing work
  while (!rootJob.Counter.IsComplete)
      TryStealAndExecute();

  // Sweep: walk rootJob's sub-DAG, return everything to pool
  rootJob.ReturnSubDag(pool);
  pool.Return(rootJob);
```

Inside the root job's `Execute()`:

```
SimTickRootJob.Execute():
  // Create sub-jobs
  jobD = pool.Rent<JoinJob>();  jobD.Counter = new JobCounter(2);
  jobD.Dependents = [this.Counter];  // when D finishes, root's counter hits zero

  jobB = pool.Rent<ComputeJob>();  jobB.Dependents = [jobD.Counter];
  jobC = pool.Rent<ComputeJob>();  jobC.Dependents = [jobD.Counter];

  // Track sub-jobs for coordinator sweep
  this.SubJobs = [jobB, jobC, jobD];

  deque.Push(jobB);
  deque.Push(jobC);
  // jobD auto-enqueues when B and C complete (counter hits zero)
```

---

## Part 2: Tagged Handles and Thread-Local Buffers

### 2A. Handle Bit Layout

`Handle<T>` stays as an `int` wrapper. The bit interpretation is contextual — only meaningful
during a work phase and the coordinator merge.

```
Global handle (bit 31 = 0):
  [0][31 bits: global arena index]
  Range: 0 to 2,147,483,647 (2B entries per type)

Local handle (bit 31 = 1):
  [1][7 bits: thread ID][24 bits: local index]
  Thread IDs: 0-127 (128 worker threads max)
  Local indices: 0 to 16,777,215 (16M nodes per thread per work phase)

Handle.None (-1 = 0xFFFFFFFF):
  Special sentinel. Fixup pass checks for -1 before attempting decode.
```

Helper methods (static, on a utility type — not on `Handle<T>` itself to keep it clean):

```csharp
static bool IsGlobal(int index) => index >= 0;
static bool IsLocal(int index) => index < 0 && index != -1;
static int EncodeLocal(int threadId, int localIndex)
    => unchecked((int)(0x80000000u | ((uint)threadId << 24) | (uint)localIndex));
static int DecodeThreadId(int index) => (index >> 24) & 0x7F;
static int DecodeLocalIndex(int index) => index & 0x00FFFFFF;
```

### 2B. ThreadLocalBuffer per Type (`Fabrica.Core/Memory/ThreadLocalBuffer.cs`)

Each worker thread gets one TLB per node type in the world. A TLB is a simple append-only array:

```csharp
internal struct ThreadLocalBuffer<T> where T : struct
{
    private T[] _data;
    private int _count;
    private readonly int _threadId;  // assigned by coordinator at phase start

    // Returns a local Handle<T> with embedded thread ID.
    public Handle<T> Allocate() { ... }

    // Coordinator reads this after the join barrier.
    public ReadOnlySpan<T> WrittenSpan => _data.AsSpan(0, _count);
}
```

Key properties:

- Append-only, no synchronization during the work phase
- Thread ID baked into every returned handle — cross-thread references are unambiguous
- Reset (not freed) between work phases for steady-state zero allocation
- Internally a growable array (may evolve to `UnsafeList<T>` like `UnsafeStack<T>` for
  unchecked perf)

### 2C. INodeHandleRewriter (Visitor Extension)

New visitor variant for in-place handle rewriting during fixup. Extends the existing
`INodeVisitor` / `INodeChildEnumerator` pattern:

```csharp
internal interface INodeHandleRewriter
{
    void Rewrite<TChild>(ref Handle<TChild> handle) where TChild : struct;
}
```

And a corresponding method on `INodeChildEnumerator<TNode>`:

```csharp
void RewriteChildren<TRewriter>(ref TNode node, ref TRewriter rewriter)
    where TRewriter : struct, INodeHandleRewriter;
```

Same struct-generic pattern — JIT specializes per rewriter type, zero virtual dispatch. Follows
the same `typeof` elimination pattern proven by JIT baselines.

---

## Part 3: Parallel Coordinator Merge

### The Key Insight

Each worker thread has one TLB **per node type**. After the job phase, the coordinator can
process node types concurrently because each type's `NodeStore` (arena + refcount table + handler)
is independent. One merge-worker per node type.

### Merge Phases

**Phase 1 — Allocate and copy (parallel by type):**

For each node type T, one worker:

1. Iterates all M thread-TLBs for type T.
2. Batch-allocates N global indices from T's `UnsafeSlabArena`.
3. Copies node data from TLBs into the arena.
4. Builds remap table: `remap[threadId][localIndex] -> globalIndex`.

All types can do this concurrently — each type writes to its own arena.

**Barrier** — all remap tables complete.

**Phase 2a — Fixup (parallel by parent type):**

For each parent type P, one worker rewrites ALL children of its newly-created nodes (regardless
of child type) using the remap tables. Safe because each parent type's nodes live in a separate
arena. This preserves the immutable-construction model: a node is fully fixed up before anyone
else reads it.

**Barrier** — all fixup complete.

**Phase 2b — Refcount (parallel by child type):**

For each child type T, one worker walks all newly-created nodes (read-only at this point, since
fixup is complete) and increments T's `RefCountTable` for each child handle of type T. Safe
because each type's `RefCountTable` is independent.

**Future optimization**: During Phase 2b, a T-worker walks ALL new nodes of every parent type. If
a parent type never references children of type T, that walk is wasted. Maintain a static
type-connectivity table (which parent types have children of which types) to skip irrelevant
parent types entirely.

### Remap Table Structure

Per-type, per-thread:

```csharp
// remap[threadId] is an array mapping localIndex -> globalIndex
int[][] remap = new int[workerCount][];
remap[threadId] = new int[tlbCount]; // filled during Phase 1
```

With 128 threads max and 16M local indices max per thread, this is bounded and predictable.

---

## Part 4: Integration with Snapshot Pipeline

After the coordinator merge completes:

1. Build `SnapshotSlice<TNode, THandler>` root sets for each type.
2. Call `IncrementRootRefCounts()` (batch increment on the new roots).
3. Enqueue composite snapshot into `ProducerConsumerQueue`.
4. Consumer dequeues, processes, calls `DecrementRootRefCounts()` (cascade-free).

This is the existing `SnapshotSlice` + `ProducerConsumerQueue` pipeline, now fed by the parallel
merge instead of a placeholder.

---

## PR Sequence

1. **Job primitives**: `JobCounter`, `Job` abstract base class with strongly-typed subclasses,
   Treiber pool (adapt PR #92). Close PR #106 (arena-coordinator — will be rebuilt).
2. **Job scheduler**: `JobScheduler` with `WorkStealingDeque` dispatch, DAG wiring, coordinator
   wait-while-stealing.
3. **Tagged handles + TLB**: Handle encoding utilities, `ThreadLocalBuffer<T>` (per-type,
   per-thread), `INodeHandleRewriter`.
4. **Coordinator merge**: Parallel-by-type merge pipeline — allocate, copy, fixup, refcount.
5. **Snapshot + PCQ integration**: Wire `SnapshotSlice` composition into the pipeline, replace
   `WorldImage` placeholder.
6. **Unified thread pool**: Replace dual `WorkerGroup` with shared `JobScheduler` (per
   `docs/unified-thread-pool.md`).

---

## Open Questions (To Resolve During Implementation)

- **Phase 2 merge optimization**: During the per-child-type refcount pass, a T-worker currently
  walks ALL new nodes. Consider adding type-connectivity metadata so workers can skip parent types
  that never reference their child type. (Tracked as a future todo.)
- **TLB sizing**: Initial capacity per TLB, growth strategy. Start with a reasonable default
  (e.g., 1024) and profile. May evolve to `UnsafeList<T>` for unchecked access performance.
- **WorkerContext shape**: Exactly what thread-specific state a job needs access to (TLBs, deque,
  thread ID, etc.) and how to pass it efficiently.
