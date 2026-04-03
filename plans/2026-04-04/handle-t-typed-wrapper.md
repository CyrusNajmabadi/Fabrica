# Handle<T> — Strongly-Typed Arena Index Wrapper

## Overview

Replace raw `int` node indices with a strongly-typed `Handle<T>` wrapper struct throughout the arena/refcount/snapshot system. This is a prerequisite for the visitor pattern which will unify DAG walking across validation, increment, and cascade-free operations.

## New Type

`src/Fabrica.Core/Memory/Handle.cs`:

```csharp
internal readonly struct Handle<T>(int index) : IEquatable<Handle<T>> where T : struct
{
    public static readonly Handle<T> None = new(-1);
    public int Index { get; } = index;
    public bool IsValid => this.Index >= 0;
}
```

- Sentinel: `Handle<T>.None` wraps `-1` (replaces the `-1` convention)
- Tests for child existence: `node.Left.IsValid` instead of `node.Left >= 0`
- Implicit conversion from `Handle<T>` to `int` is intentionally **not** provided to prevent accidental mixing
- Implements `IEquatable<Handle<T>>`, `==`/`!=` operators, `GetHashCode()` for dictionary key usage

## Changes by File

### Production code

- **`UnsafeSlabArena<T>`**
  - `Allocate()` returns `Handle<T>` instead of `int`
  - `this[Handle<T> handle]` indexer (delegates to `_directory[handle.Index]`)
  - `Free(Handle<T> handle)` instead of `Free(int)`
  - Free list: `UnsafeStack<Handle<T>>` instead of `UnsafeStack<int>`
  - Debug asserts use `handle.Index`

- **`RefCountTable`** → **`RefCountTable<T>`**
  - Becomes `RefCountTable<T> where T : struct` — prevents accidentally using the wrong table for a node type
  - `GetCount(Handle<T>)`, `Increment(Handle<T>)`, `Decrement<THandler>(Handle<T>, THandler)`
  - `IncrementBatch(ReadOnlySpan<Handle<T>>)`, `DecrementBatch<THandler>(ReadOnlySpan<Handle<T>>, THandler)`
  - `IRefCountHandler.OnFreed(Handle<T> handle, RefCountTable<T> table)` — handler receives typed handle
  - `EnsureCapacity(int highWater)` stays `int` — it's a numeric count, not a handle
  - Internal storage (`UnsafeSlabDirectory<int>`) unchanged — stores `int` refcounts, indexed by `handle.Index`
  - Cascade pending stack: `UnsafeStack<Handle<T>>` instead of `UnsafeStack<int>`

- **`NodeStore<TNode, THandler>`**
  - Now bundles `UnsafeSlabArena<TNode>` + `RefCountTable<TNode>` — both parameterized on same type
  - `IncrementRoots(ReadOnlySpan<Handle<TNode>>)` passes directly to `RefCounts.IncrementBatch`
  - `DecrementRoots(ReadOnlySpan<Handle<TNode>>)` — same
  - Debug root tracking updated to use `Handle<TNode>` as dictionary key

- **`SnapshotSlice<TNode, THandler>`**
  - `AddRoot(Handle<TNode>)` instead of `AddRoot(int)`
  - Internal `List<Handle<TNode>>` instead of `List<int>`
  - `Roots` returns `ReadOnlySpan<Handle<TNode>>`

- **`DagValidator`**
  - `IChildEnumerator<TNode>.GetChildren(in TNode node, List<Handle<TNode>> children)` — same-store children
  - `AssertValid` / `Validate` take `ReadOnlySpan<Handle<TNode>> roots`
  - Internal arrays still indexed by `int` (the handle's `.Index` is used for array access)

### Tests — node struct changes

All test node structs change from `int Left` to `Handle<TreeNode> Left`, etc.:

- `NodeStoreTests.cs`
- `SnapshotSliceTests.cs`
- `SnapshotLifecycleTests.cs`
- `DagValidatorTests.cs`
- `CrossTypeSnapshotTests.cs` — `ChildRef` becomes `Handle<ChildNode>`
- `SlabArenaTests.cs`
- `RefCountTableTests.cs` — `RefCountTable` → `RefCountTable<DummyNode>`, handlers receive `Handle<DummyNode>`

### Benchmarks

- `PersistentTreeBenchmarks.cs` — `TreeNode` uses `Handle<TreeNode>` for Left/Right
- `SlabArenaBenchmarks.cs` — `Allocate()` returns `Handle<T>`
- `RefCountTableBenchmarks.cs` — `RefCountTable<DummyNode>`, handlers use `Handle<DummyNode>`

## Key Design Decisions

- **No implicit conversion** `Handle<T> → int`. Forces explicit `.Index` access, which is the whole point of type safety.
- **`RefCountTable<T>`** — generic on the node type, so `RefCountTable<MeshNode>` can't be accidentally used with `Handle<MaterialNode>`.
- **`Handle<T>` is `readonly struct`** — zero allocation, same size as `int`, JIT optimizes away the wrapper (confirmed by experiment PR #110).
- **Sentinel is `.None` not `.Invalid`** — matches the "no child" concept.
- **`EnsureCapacity` stays `int`** — it takes a numeric count (high-water mark), not a node handle.

## What This Does NOT Cover (deferred: Visitor Pattern)

- Unifying `IRefCountHandler` and `IChildEnumerator` into a single walker/visitor model
- The "world context" / "store registry" design for cross-type traversal
- Snapshot composition patterns

## Outcome

Implemented as PR #111. All 756 tests pass, benchmarks compile, `dotnet format` clean. 18 files changed (+1,084 / -1,317 lines). Experiment artifacts (`RefCountTableGeneric.cs`, `RefCountTableGenericBenchmarks.cs`) deleted.
