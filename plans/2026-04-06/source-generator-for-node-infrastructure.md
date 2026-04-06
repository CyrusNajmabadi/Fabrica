# Source Generator for Node Infrastructure

## Problem

The system has many per-node-type components that must be created and wired together:

| Component | Per node type | Wired into |
|---|---|---|
| `NodeStore<TNode, THandler>` | 1 | Snapshot, coordinator |
| `RefCountTable<TNode>` | 1 | NodeStore |
| `UnsafeSlabArena<TNode>` | 1 | NodeStore |
| `SnapshotSlice<TNode, THandler>` | 1 per snapshot | Snapshot |
| `INodeChildEnumerator<TNode>` impl | 1 | NodeStore, handlers, merge |
| `IRefCountHandler` impl | 1 | NodeStore (cross-type decrement) |
| `INodeVisitor` dispatch struct | 1 | Handler (typeof switches) |
| `ThreadLocalBuffer<TNode>` | 1 per worker per type | Worker context, merge |
| Coordinator merge loop for type | 1 | Coordinator |
| `DagValidator.IWorldAccessor` impl | 1 per world | Validation |

Today, adding a node type like `Belt` requires manually updating every one of these touchpoints.
The `CrossTypeSnapshotTests` show the concrete cost: ~100 lines of handler/enumerator/visitor
boilerplate just to wire two types together, and that's in a test — production code with 20+ types
would be unmaintainable.

Worse, the coordinator merge phase needs to know which DAGs can reach which types to avoid
wasted work (see `type-reachability-for-merge-skip.md`). Solving this manually requires either
fragile runtime recursion (Approach A, which breaks with cycles) or hand-maintained lookup tables.

## Solution: Source Generator with `[Node]` and `[Snapshot]`

A Roslyn incremental source generator analyzes node struct declarations at compile time and
emits all the boilerplate. Two attributes drive it:

### `[Node]` — marks a node struct

```csharp
[Node]
[StructLayout(LayoutKind.Sequential)]
public partial struct Belt
{
    public Handle<Belt> Next;
    public Handle<Inserter> Input;
    public Handle<Inserter> Output;
    public int Speed;
}
```

The generator inspects all `Handle<T>` fields to discover the child types. Non-handle fields
(like `Speed`) are ignored. The struct must be `partial` so the generator can add methods.

### `[Snapshot]` — marks the composite snapshot type

```csharp
[Snapshot]
public partial class GameSnapshot
{
    // Generator discovers all [Node] types in the assembly and emits:
    //   - A SnapshotSlice<TNode, THandler> field per node type
    //   - IncrementAll() / DecrementAll() that hit every slice
    //   - Clear() for reuse
}
```

## What Gets Generated

### 1. `INodeChildEnumerator<TNode>` implementations

For each `[Node]` type, the generator emits a struct implementing the enumerator:

```csharp
// Generated
internal struct BeltChildEnumerator : INodeChildEnumerator<Belt>
{
    public readonly void EnumerateChildren<TVisitor>(in Belt node, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor
    {
        if (node.Next.IsValid) visitor.Visit(node.Next);
        if (node.Input.IsValid) visitor.Visit(node.Input);
        if (node.Output.IsValid) visitor.Visit(node.Output);
    }

    public void EnumerateRefChildren<TVisitor>(ref Belt node, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor
    {
        if (node.Next.Index != -1) visitor.VisitRef(ref node.Next);
        if (node.Input.Index != -1) visitor.VisitRef(ref node.Input);
        if (node.Output.Index != -1) visitor.VisitRef(ref node.Output);
    }
}
```

The context overloads follow the same pattern.

### 2. `IRefCountHandler` implementations (cross-type cascade-free)

For each `[Node]` type, the generator emits a handler struct that dispatches child decrements
to the correct `RefCountTable`. The handler captures references to every `NodeStore` that the
node's children live in:

```csharp
// Generated
internal struct BeltRefCountHandler(
    UnsafeSlabArena<Belt> arena,
    NodeStore<Belt, BeltRefCountHandler> beltStore,
    NodeStore<Inserter, InserterRefCountHandler> inserterStore)
    : RefCountTable<Belt>.IRefCountHandler
{
    public readonly void OnFreed(Handle<Belt> handle, RefCountTable<Belt> table)
    {
        ref readonly var node = ref arena[handle];
        var visitor = new BeltDecrementVisitor(beltStore, inserterStore);
        var enumerator = new BeltChildEnumerator();
        enumerator.EnumerateChildren(in node, ref visitor);
        arena.Free(handle);
    }
}
```

### 3. `INodeVisitor` dispatch structs (typeof switches)

```csharp
// Generated
internal struct BeltDecrementVisitor(
    NodeStore<Belt, BeltRefCountHandler> beltStore,
    NodeStore<Inserter, InserterRefCountHandler> inserterStore) : INodeVisitor
{
    public readonly void Visit<TChild>(Handle<TChild> child) where TChild : struct
    {
        if (typeof(TChild) == typeof(Belt))
            beltStore.DecrementRefCount(Unsafe.As<Handle<TChild>, Handle<Belt>>(ref child));
        else if (typeof(TChild) == typeof(Inserter))
            inserterStore.DecrementRefCount(Unsafe.As<Handle<TChild>, Handle<Inserter>>(ref child));
    }
}
```

The JIT eliminates dead branches since each `typeof` comparison is a constant.

### 4. Type reachability (compile-time graph analysis)

The generator builds the full type reference graph from `Handle<T>` fields, runs DFS with a
visited set (handling cycles correctly), and computes the reachable set for each root type.

This replaces the `IsOrCanReachChildOfType<T>()` approach entirely. Instead of runtime recursion,
the generator emits the coordinator merge code directly, with unreachable combinations skipped:

```csharp
// Generated — coordinator merge for type Belt
// Walks all DAGs whose root type can reach Belt nodes.
//
// Reachability:
//   Belt       → { Belt, Inserter }         ✓ can reach Belt
//   Inserter   → { Inserter }               ✗ skip — cannot reach Belt
//   Assembler  → { Assembler, Inserter }    ✗ skip — cannot reach Belt
//   PowerPole  → { PowerPole, Belt }        ✓ can reach Belt
internal static void MergeBeltNodes(CoordinatorContext ctx)
{
    // Only walk DAGs rooted at types that can reach Belt:
    MergeBeltFromDag<Belt, BeltChildEnumerator>(ctx);
    MergeBeltFromDag<PowerPole, PowerPoleChildEnumerator>(ctx);

    // Skipped (cannot reach Belt):
    // - Inserter DAGs
    // - Assembler DAGs
}
```

Because this is computed at compile time:
- **Cycles are handled correctly** (DFS with visited set)
- **No runtime cost** — unreachable paths aren't even compiled in
- **Self-documenting** — generated comments explain what was skipped and why

### 5. Snapshot composition

```csharp
// Generated partial class additions
public partial class GameSnapshot
{
    // One slice per node type
    private SnapshotSlice<Belt, BeltRefCountHandler> _beltSlice;
    private SnapshotSlice<Inserter, InserterRefCountHandler> _inserterSlice;
    private SnapshotSlice<Assembler, AssemblerRefCountHandler> _assemblerSlice;
    private SnapshotSlice<PowerPole, PowerPoleRefCountHandler> _powerPoleSlice;

    public void IncrementAll()
    {
        _beltSlice.IncrementRootRefCounts();
        _inserterSlice.IncrementRootRefCounts();
        _assemblerSlice.IncrementRootRefCounts();
        _powerPoleSlice.IncrementRootRefCounts();
    }

    public void DecrementAll()
    {
        _beltSlice.DecrementRootRefCounts();
        _inserterSlice.DecrementRootRefCounts();
        _assemblerSlice.DecrementRootRefCounts();
        _powerPoleSlice.DecrementRootRefCounts();
    }
}
```

### 6. ThreadLocalBuffer allocation per worker

```csharp
// Generated
internal partial class WorkerNodeBuffers
{
    public ThreadLocalBuffer<Belt> Belts { get; }
    public ThreadLocalBuffer<Inserter> Inserters { get; }
    public ThreadLocalBuffer<Assembler> Assemblers { get; }
    public ThreadLocalBuffer<PowerPole> PowerPoles { get; }

    public WorkerNodeBuffers(int threadId)
    {
        Belts = new ThreadLocalBuffer<Belt>(threadId);
        Inserters = new ThreadLocalBuffer<Inserter>(threadId);
        Assemblers = new ThreadLocalBuffer<Assembler>(threadId);
        PowerPoles = new ThreadLocalBuffer<PowerPole>(threadId);
    }

    public void ResetAll()
    {
        Belts.Reset();
        Inserters.Reset();
        Assemblers.Reset();
        PowerPoles.Reset();
    }
}
```

### 7. DagValidator.IWorldAccessor (debug/test)

```csharp
// Generated
internal struct GeneratedWorldAccessor(
    NodeStore<Belt, BeltRefCountHandler> beltStore,
    NodeStore<Inserter, InserterRefCountHandler> inserterStore,
    /* ... */) : DagValidator.IWorldAccessor
{
    public int TypeCount => 4; // number of [Node] types

    public int HighWater(int typeId) => typeId switch
    {
        0 => beltStore.Arena.GetTestAccessor().HighWater,
        1 => inserterStore.Arena.GetTestAccessor().HighWater,
        // ...
        _ => 0,
    };

    // ... GetRefCount, GetChildren follow the same pattern
}
```

## What the User Writes vs. What Gets Generated

**User writes (per node type):**
```csharp
[Node]
[StructLayout(LayoutKind.Sequential)]
public partial struct Belt
{
    public Handle<Belt> Next;
    public Handle<Inserter> Input;
    public Handle<Inserter> Output;
    public int Speed;
}
```

**User writes (once):**
```csharp
[Snapshot]
public partial class GameSnapshot { }
```

**Generator produces:**
- `BeltChildEnumerator` (INodeChildEnumerator)
- `BeltRefCountHandler` (IRefCountHandler)
- `BeltDecrementVisitor` (INodeVisitor)
- Partial `GameSnapshot` with `SnapshotSlice<Belt, ...>` field
- Partial `WorkerNodeBuffers` with `ThreadLocalBuffer<Belt>`
- Coordinator merge method for Belt with reachability-based skip logic
- `GeneratedWorldAccessor` updated with Belt store

Adding a new node type = adding one struct with `[Node]`. Everything else follows.

## Project Structure

```
src/
  Fabrica.Core/              — runtime types (Handle<T>, NodeStore, etc.)
  Fabrica.Generators/        — the source generator assembly
    NodeGenerator.cs          — incremental generator entry point
    NodeAnalyzer.cs           — extracts Handle<T> fields, builds type graph
    ReachabilityAnalysis.cs   — DFS reachability with cycle handling
    Emitters/
      ChildEnumeratorEmitter.cs
      RefCountHandlerEmitter.cs
      DecrementVisitorEmitter.cs
      SnapshotEmitter.cs
      WorkerBuffersEmitter.cs
      MergeEmitter.cs
      WorldAccessorEmitter.cs
```

The generator project references `Microsoft.CodeAnalysis.CSharp` and is referenced
as an analyzer from `Fabrica.Core.csproj`:

```xml
<ProjectReference Include="..\Fabrica.Generators\Fabrica.Generators.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
```

## Incremental Generator Design

Use Roslyn's `IIncrementalGenerator` for efficient incremental compilation:

1. **Syntax provider**: filter for structs with `[Node]` attribute
2. **Semantic transform**: extract `Handle<T>` fields, resolve child types
3. **Combine**: build the full type graph from all node types
4. **Reachability**: DFS from each type to compute reachable sets
5. **Emit**: generate source for each category above

The incremental pipeline ensures re-generation only when a `[Node]` struct's handle fields change.

## Supersedes

This plan supersedes `type-reachability-for-merge-skip.md`. The reachability analysis becomes
one component of the broader generator, and Approach A (runtime static generics) is no longer
needed since the generator handles cycles at compile time.

## Open Questions

1. **Assembly boundaries**: all `[Node]` types must be visible to the generator. If node types
   span multiple assemblies, we'd need either `InternalsVisibleTo` or a two-pass approach where
   each assembly generates its own infrastructure and a final assembly composes them.

2. **Custom handler logic**: some `IRefCountHandler.OnFreed` implementations may need custom
   behavior beyond the standard "decrement children + free." The generator could emit a partial
   method hook (`OnNodeFreed`) that the user optionally implements.

3. **Validation toggle**: the debug-only `EnableValidation` call and `IWorldAccessor` could be
   behind a generator flag or always emitted behind `#if DEBUG`.

4. **Ordering**: does the generator need to emit type IDs in a stable order? Probably yes, for
   `DagValidator.IWorldAccessor.TypeCount` and `HighWater(int typeId)` dispatch.
