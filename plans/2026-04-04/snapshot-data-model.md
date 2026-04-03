# Snapshot Data Model — Implementation Plan

## Context

Builds on `UnsafeSlabArena<T>` (PR #98) and `RefCountTable` (PR #100). Formalizes the "snapshot"
concept: an immutable view of heterogeneous, acyclic DAGs with typed root sets.

## New Types

### `NodeStore<TNode, THandler>` — `src/Fabrica.Core/Memory/NodeStore.cs`

- Owns `UnsafeSlabArena<TNode>`, `RefCountTable`, and a `THandler` instance.
- `IncrementRoots(ReadOnlySpan<int>)` — batch-increments root refcounts.
- `DecrementRoots(ReadOnlySpan<int>)` — batch-decrements with cascade via stored handler.
- `SingleThreadedOwner` for debug assertions.
- `TestAccessor` for test introspection.

### `SnapshotSlice<TNode, THandler>` — `src/Fabrica.Core/Memory/SnapshotSlice.cs`

- Holds `NodeStore<TNode, THandler>` reference + `List<int>` root indices.
- `AddRoot(int)`, `Roots` (ReadOnlySpan), `Clear()`.
- `IncrementRootRefCounts()` — delegates to store.
- `DecrementRootRefCounts()` — delegates to store.

## Tests — `tests/Fabrica.Core.Tests/Memory/`

### `NodeStoreTests.cs`
- Basic increment/decrement roots.
- Cascade through single-type trees.

### `SnapshotSliceTests.cs`
- Single-type lifecycle: build tree, create slice, increment, verify, decrement, verify.
- Multi-snapshot sharing: two slices sharing subtrees, release in both orders.
- Many roots per snapshot: 2-3 roots, some shared.
- Permutation testing on release order.

### `CrossTypeSnapshotTests.cs`
- Type A nodes referencing type B nodes (different stores).
- Release A roots, verify B subtrees survive.
- Full cross-cascade: A → B → A.

### `SnapshotLifecycleTests.cs`
- Simulated PCQ flow: create N snapshots, increment roots, release oldest-first.
- Verify steady-state: freed nodes recycled, refcounts correct at each step.
- Various release orders (FIFO, LIFO, random permutations).

## Non-Goals (This PR)

- Coordinator / ThreadLocalBuffer redesign (separate PR, depends on this).
- Actual PCQ integration (tests simulate the lifecycle, don't use real PCQ).
- Root demotion (coordinator concern).
- Benchmarks (the types are thin wrappers; perf is dominated by arena/refcount ops already benchmarked).
