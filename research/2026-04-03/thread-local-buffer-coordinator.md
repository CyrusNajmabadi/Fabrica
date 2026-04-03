# ThreadLocalBuffer + ArenaCoordinator Design

## Overview

This completes the arena-backed persistent data structure pipeline: workers create new nodes in
thread-local buffers (no synchronization), then a single-threaded coordinator merges those buffers
into the global `UnsafeSlabArena<T>` + `RefCountTable` system.

## Tag Bit Convention

Workers reference both existing global nodes and newly-created local nodes. A tag bit in the high
position distinguishes them:

- `>= 0`: global index (existing node in the shared arena)
- `== -1`: no child sentinel (matches the existing -1 convention in tree nodes)
- `< -1` (i.e., `<= -2`): tagged local index — extract via `& 0x7FFF_FFFF`

This works because local index 0 tagged is `0x80000000 = int.MinValue = -2147483648`, which is
`< -1`. The maximum local index is `0x7FFFFFFE` (tags to -2). Index `0x7FFFFFFF` would tag to -1
(the sentinel), so it is reserved — but local buffers will never hold 2 billion entries.

The `ArenaIndex` static helper encapsulates this convention.

## ThreadLocalBuffer

Append-only per-thread buffer. Each worker gets one before the work phase and returns it at join.

- **New nodes**: `T[]` + `_count`. Workers call `Append(T)` to get a local index (untagged).
  They tag it with `ArenaIndex.TagLocal(idx)` when storing it as a child reference in another node.
- **Release log**: `UnsafeStack<int>` of global indices the worker wants to release (old roots).
  Reuses the existing `UnsafeStack<int>` type rather than a hand-rolled list.
- **Clear for reuse**: `Clear()` resets `_count` and drains the release stack. Backing arrays are
  kept to avoid reallocation in steady state.

SingleThreadedOwner asserts one worker per buffer in debug builds.

## IArenaNode

Struct-constrained interface (same pattern as `IRefCountHandler`):

```csharp
interface IArenaNode
{
    void FixupReferences(ReadOnlySpan<int> localToGlobalMap);
    void IncrementChildren(RefCountTable table);
}
```

- `FixupReferences`: for each child field, if `ArenaIndex.IsLocal(child)`, replace with
  `localToGlobalMap[ArenaIndex.UntagLocal(child)]`.
- `IncrementChildren`: for each valid child (not NoChild), call `table.Increment(child)`.

Both methods are implemented by the concrete node struct. The JIT specializes per struct type.

## ArenaCoordinator Merge Pipeline

The coordinator owns the `UnsafeSlabArena<TNode>` and `RefCountTable`. The `Merge` method:

1. **Allocate global indices** for every new node across all buffers. Uses a reusable `int[]` field
   (`_localToGlobalMap`) that grows as needed but never shrinks — avoids per-merge allocation.
2. **Copy + fixup**: for each buffer, copy each node to its global slot, call `FixupReferences`
   with the remap, then call `IncrementChildren` to establish refcounts.
3. **EnsureCapacity**: called on the RefCountTable after allocating globals, before any refcount
   operations.
4. **Process releases**: collect all release logs from all buffers, call `DecrementBatch` or
   individual `Decrement` with the cascade handler.

The merge method takes a struct `THandler : IRefCountHandler` for the cascade-free path (same
pattern as `RefCountTable.Decrement<THandler>`).

## Key Design Decisions

- **No per-merge allocations**: the localToGlobalMap is a field, grown once. The release processing
  uses the existing cascade machinery (reusable `_cascadePending` stack in RefCountTable).
- **Buffer release log uses UnsafeStack<int>**: reuses the existing type rather than duplicating a
  growable list.
- **IArenaNode has IncrementChildren**: the coordinator calls this after fixup to increment
  refcounts for all children of new nodes. This is simpler than a separate "increment log" because
  the coordinator already iterates over all new nodes anyway.
- **Combined PR3+PR4**: the components are tightly coupled and deliver value only together.

## Portability

- `ThreadLocalBuffer<T>` → `thread_local` storage + `Vec<T>` in Rust, per-thread array in C++.
- `ArenaCoordinator` → coordinator takes ownership of thread buffers at join point.
- `ArenaIndex` tag convention → identical bit manipulation in any language.
- No GC reliance. All storage is value types in arrays.
