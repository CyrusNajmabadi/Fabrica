# Jobs, Coordinators, and the Full Pipeline

## Why We Needed SnapshotSlice First

The entire engine pipeline is a **producer/consumer** architecture where immutable world-state
snapshots flow from the simulation thread to the render thread via a `ProducerConsumerQueue`.
The critical question the snapshot work answered: **what is the unit of transfer, and how do
its lifetimes work?**

Before building jobs or coordinators, we needed to establish:

1. **What gets published**: A snapshot is a set of **root handles** into shared, refcounted
   arenas. `SnapshotSlice<TNode, THandler>` is one typed root set; a composite snapshot is an
   explicit struct of slices (one per node type).

2. **Lifetime protocol**: Publish = `IncrementRootRefCounts` (snapshot "holds" its roots).
   Retire = `DecrementRootRefCounts` (cascade-free reclaims unreachable nodes). This is the
   PCQ boundary contract.

3. **Single-threaded coordinator owns mutation**: All refcount operations (`RefCountTable`),
   arena allocation (`UnsafeSlabArena`), and cascade-free happen on one thread. Workers never
   touch refcounts directly. This is why `SingleThreadedOwner` exists and why the coordinator
   pattern matters.

4. **Cross-type DAGs**: Handlers in `NodeStore` hold references to other stores. When a node
   is freed, its handler decrements children in potentially different arenas. The visitor
   pattern (`INodeVisitor`, `INodeChildEnumerator`) unified this traversal with zero overhead
   (proven by JIT baselines).

5. **Structural vs root refcounts**: Building/editing a DAG creates structural refs (parent
   holds child). Publishing a snapshot adds root refs. Both use the same `RefCountTable` — the
   distinction is semantic, not mechanical. This separation means the coordinator can batch
   structural refcount work at merge time, then separately publish roots.

Without this foundation, we couldn't design how the coordinator publishes merged state, how
cascade-free interacts with cross-type DAGs, or what the thread ownership model looks like.

### Data flow

```
  [Parallel Workers]
       |  |  |
       v  v  v
  ThreadLocalBuffers (append-only, no contention)
       |
       v
  ArenaCoordinator (single-threaded merge)
    - allocate global slots from NodeStore.Arena
    - copy + fixup references (INodeChildEnumerator)
    - structural refcount increments (visitor pattern)
       |
       v
  NodeStore (arena + refcounts + handler)
       |
       v
  SnapshotSlice (build root set)
    - IncrementRootRefCounts (publish)
       |
       v
  ProducerConsumerQueue
       |
       v
  Consumer (process snapshot)
    - DecrementRootRefCounts (retire, cascade-free)
```

## Status of Existing PRs

### PR #106 — ArenaCoordinator (feature/arena-coordinator)

**Concepts still valid**: thread-local append-only buffers, tag-bit local/global index
distinction, single-threaded merge with fixup, deferred refcount processing.

**Needs rework**: Built before `Handle<T>`, visitor pattern, `NodeStore`, `SnapshotSlice`, and
`DagValidator`. Uses raw `int` indices with tag bits (`ArenaIndex`), its own `IArenaNode`
interface (not the visitor pattern), and doesn't integrate with the `NodeStore`/`SnapshotSlice`
lifecycle. The merge/fixup logic is sound but the types need to be rebuilt on the new
foundations.

**Recommendation**: Close PR #106. Reuse the design and test coverage, but reimplement on top
of `Handle<T>`, `NodeStore`, and the visitor pattern. The 237 tests are valuable as a
specification.

### PRs #92 and #93 — Job Pool Options

**PR #92 (Option A)**: Lock-free shared Treiber stack. Any thread rents/returns. Single CAS
per operation. `Job` has intrusive `_poolNext` field.

**PR #93 (Option B)**: Per-thread deques via `WorkStealingDeque`. Zero-synchronization on hot
path (thread-local push/pop). Cross-thread rent scans/steals. `Job` has no `_poolNext`.

**These are independent of the snapshot model** — they're about scheduling, not data. Both are
viable. The research synthesis recommended Treiber stack for job *object pooling* and
work-stealing deques for job *dispatch* — potentially using both for different purposes.

**Decision needed**: Which option, or a hybrid?

### Existing branches to close

- `feature/arena-coordinator` (#106) — reimplement on new foundations
- One of `feature/job-pool-option-a` (#92) or `feature/job-pool-option-b` (#93) — close the
  rejected option

## Proposed Implementation Order

### Phase 1: Job primitives

Pick a job pool design and land it. This is a leaf dependency — nothing else blocks on it, and
it doesn't depend on the snapshot model. The `WorkStealingDeque` already exists in Core (tested
but unused by engine).

### Phase 2: ThreadLocalBuffer on Handle\<T\>

Rebuild `ThreadLocalBuffer<T>` using `Handle<T>` instead of raw `int` + tag bits. The tag-bit
approach (`ArenaIndex`) was a pre-`Handle<T>` design. With `Handle<T>`, we can use a sentinel
value or a separate mapping for local-to-global index translation. Key design: workers append
to local buffers; at the join barrier, the coordinator drains them.

### Phase 3: ArenaCoordinator on NodeStore

Rebuild the coordinator merge pipeline on top of `NodeStore`:
- Allocate global slots from `NodeStore.Arena`
- Copy + fixup references (using `INodeChildEnumerator` instead of the old
  `IArenaNode.FixupReferences`)
- Structural refcount increments via the visitor pattern
- Process release log through `NodeStore.DecrementRoots`

This is where the visitor pattern pays off — the same traversal interface used for validation
and cascade-free also drives the fixup/increment phase.

### Phase 4: Snapshot + PCQ integration

Wire `SnapshotSlice` composition into the producer/consumer pipeline:
- Producer: coordinator merges -> assembles root sets -> `IncrementRootRefCounts` -> enqueue
  composite snapshot
- Consumer: dequeue -> process -> `DecrementRootRefCounts` (cascade-free)
- Replace the current `WorldImage` placeholder with real `NodeStore`/`SnapshotSlice` wiring

### Phase 5: Unified thread pool

Replace the two `WorkerGroup` instances (sim + render) with a unified pool, per the design in
`docs/unified-thread-pool.md`. Workers hold both executors, adaptive split per frame. This is
where the job pool from Phase 1 gets wired into the engine.

## Open Decisions

1. **Job pool**: Option A (Treiber stack), Option B (per-thread deques), or hybrid? The
   research synthesis suggests Treiber for object pooling and work-stealing for dispatch.
   Should we land both, or pick one to start?

2. **Coordinator rebuild scope**: Rebuild from scratch on the new foundations (cleaner but more
   work), or adapt PR #106 code (faster but may carry pre-Handle\<T\> design debt)?
