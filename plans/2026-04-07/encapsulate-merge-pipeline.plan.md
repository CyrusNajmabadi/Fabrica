---
name: Encapsulate merge pipeline
overview: Push RewriteAndIncrementRefCounts behind an abstract method on the base GlobalNodeStore so MergeCoordinator can orchestrate the full drain-rewrite-refcount pipeline. The game layer no longer constructs refcount visitors or calls per-store merge methods.
todos:
  - id: inode-ops-method
    content: Add IncrementChildRefCounts(in TNode) to INodeOps<TNode>
    status: completed
  - id: game-node-ops
    content: Implement IncrementChildRefCounts in GameNodeOps for all three node types
    status: completed
  - id: abstract-base
    content: Add abstract RewriteAndIncrementRefCounts() on base GlobalNodeStore; override in generic subclass using _nodeOps.IncrementChildRefCounts
    status: completed
  - id: testaccessor-generic
    content: Move generic RewriteAndIncrementRefCounts<TRefcountVisitor>(start, count, visitor) to TestAccessor
    status: completed
  - id: merge-coordinator
    content: Add MergeAll() to MergeCoordinator (drain all + rewrite+refcount all)
    status: completed
  - id: game-producer
    content: Simplify GameProducer to use coordinator.MergeAll(); delete GameRefcountVisitor
    status: completed
  - id: update-tests
    content: "Update CoordinatorMergeTests, JobMergePipelineTests, GamePipelineTests: add IncrementChildRefCounts to test ops, remove RefcountVisitor structs, update callers"
    status: completed
  - id: sim-producer-docs
    content: Update SimulationProducer doc comments to reference MergeAll()
    status: completed
isProject: false
---

# Encapsulate Merge Pipeline into MergeCoordinator

## Core idea

Add `IncrementChildRefCounts(in TNode node)` to `INodeOps<TNode>` so each store can internally handle the refcount-increment phase. This eliminates the need for an external `GameRefcountVisitor` — the behavior moves into `GameNodeOps`, which already captures all three stores. The parameterless `RewriteAndIncrementRefCounts()` becomes abstract on the base `GlobalNodeStore`, letting `MergeCoordinator.MergeAll()` orchestrate the full pipeline.

## Changes

### 1. Add `IncrementChildRefCounts` to `INodeOps<TNode>`

In [src/Fabrica.Core/Memory/Nodes/INodeOps.cs](src/Fabrica.Core/Memory/Nodes/INodeOps.cs), add a new method:

```csharp
void IncrementChildRefCounts(in TNode node)
    => throw new NotImplementedException();
```

This is the increment counterpart to the existing `EnumerateChildren` + visitor pattern. The implementation calls `IncrementRefCount` on the appropriate store for each valid child handle. The child enumeration knowledge is already in the same struct, so they stay in sync.

### 2. Implement in `GameNodeOps`

In [src/Fabrica.Game/Nodes/GameNodeOps.cs](src/Fabrica.Game/Nodes/GameNodeOps.cs), add implementations for all three node types. Each just calls `IncrementRefCount` on the correct store per child:

```csharp
void INodeOps<MachineNode>.IncrementChildRefCounts(in MachineNode node)
{
    if (node.InputBelt.IsValid) BeltStore.IncrementRefCount(node.InputBelt);
    if (node.OutputBelt.IsValid) BeltStore.IncrementRefCount(node.OutputBelt);
}
```

(Same pattern for `BeltSegmentNode` and `ItemNode`.)

### 3. Add abstract methods on base `GlobalNodeStore`

In [src/Fabrica.Core/Memory/GlobalNodeStore.cs](src/Fabrica.Core/Memory/GlobalNodeStore.cs), the base class becomes:

```csharp
public abstract class GlobalNodeStore
{
    internal abstract void Drain();
    internal abstract void RewriteAndIncrementRefCounts();
    public abstract void ResetMergeState();
}
```

`GlobalNodeStore<TNode, TNodeOps>` overrides `RewriteAndIncrementRefCounts()` — the parameterless version already exists and is self-contained (uses `_lastDrainStart`/`_lastDrainCount` + `_nodeOps`). The refcount loop changes from `_nodeOps.EnumerateChildren(in node, ref refcountVisitor)` to `_nodeOps.IncrementChildRefCounts(in node)`.

### 4. Move generic `RewriteAndIncrementRefCounts<TRefcountVisitor>` to TestAccessor

The generic overload that takes an explicit visitor and start/count range is only needed by tests. Move it behind `TestAccessor` so the public API is just the parameterless abstract version.

### 5. Add `MergeAll()` to `MergeCoordinator`

In [src/Fabrica.Core/Memory/MergeCoordinator.cs](src/Fabrica.Core/Memory/MergeCoordinator.cs):

```csharp
public void MergeAll()
{
    foreach (var store in _stores) store.Drain();
    foreach (var store in _stores) store.RewriteAndIncrementRefCounts();
}
```

Two separate loops enforce the drain barrier (all remap tables populated before any rewrite begins).

### 6. Simplify `GameProducer`

In [src/Fabrica.Game/GameProducer.cs](src/Fabrica.Game/GameProducer.cs), the merge section collapses from:

```csharp
tickState.Coordinator.DrainAll();
var refcountVisitor = new GameRefcountVisitor { ... };
tickState.ItemStore.RewriteAndIncrementRefCounts(ref refcountVisitor);
tickState.BeltStore.RewriteAndIncrementRefCounts(ref refcountVisitor);
tickState.MachineStore.RewriteAndIncrementRefCounts(ref refcountVisitor);
```

to:

```csharp
tickState.Coordinator.MergeAll();
```

The game layer still calls `BuildSnapshotSlice()` per store (type-specific return value) and `ResetAll()`. These are simple per-store operations with no cross-store ordering concerns.

### 7. Delete `GameRefcountVisitor`

[src/Fabrica.Game/Nodes/GameRefcountVisitor.cs](src/Fabrica.Game/Nodes/GameRefcountVisitor.cs) is no longer needed — its behavior is now inside `GameNodeOps.IncrementChildRefCounts`.

### 8. Update `SimulationProducer` doc comments

[src/Fabrica.Engine/Simulation/SimulationProducer.cs](src/Fabrica.Engine/Simulation/SimulationProducer.cs) has TODO comments referencing the old API. Update to reference `coordinator.MergeAll()`.

### 9. Update test node ops and test callers

- `**CoordinatorMergeTests.MergeNodeOps**` and `**JobMergePipelineTests.TestNodeOps**`: add `IncrementChildRefCounts` implementations (same pattern as game node ops).
- `**CoordinatorMergeTests.RefcountVisitor**` and `**JobMergePipelineTests.RefcountVisitor**`: these separate test visitor structs become unnecessary — delete them. Tests can use the parameterless `store.RewriteAndIncrementRefCounts()` since `_nodeOps` will handle it.
- `**GamePipelineTests**`: replace explicit refcount visitor + per-store calls with `coordinator.MergeAll()` (or parameterless per-store calls if the test doesn't use a coordinator).

### Files touched

**Production:**

- `src/Fabrica.Core/Memory/Nodes/INodeOps.cs` — add method
- `src/Fabrica.Core/Memory/GlobalNodeStore.cs` — abstract method on base, move generic overload to TestAccessor, override uses `IncrementChildRefCounts`
- `src/Fabrica.Core/Memory/MergeCoordinator.cs` — add `MergeAll()`
- `src/Fabrica.Game/Nodes/GameNodeOps.cs` — implement `IncrementChildRefCounts`
- `src/Fabrica.Game/GameProducer.cs` — simplify to `MergeAll()`
- `src/Fabrica.Game/Nodes/GameRefcountVisitor.cs` — **delete**
- `src/Fabrica.Engine/Simulation/SimulationProducer.cs` — update comments

**Tests:**

- `tests/Fabrica.Core.Tests/Memory/CoordinatorMergeTests.cs` — add to `MergeNodeOps`, remove `RefcountVisitor`, update callers
- `tests/Fabrica.Core.Tests/Memory/JobMergePipelineTests.cs` — same pattern
- `tests/Fabrica.Game.Tests/GamePipelineTests.cs` — use parameterless version or `MergeAll()`

