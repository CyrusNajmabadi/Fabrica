# Type Reachability Analysis for Coordinator Merge Optimization

## Problem

During the coordinator merge phase, the plan is to assign one pseudo-worker per node type.
Each worker walks all newly-created DAGs, looking for children of its assigned type to rewrite
local handles → global handles and update refcounts. Since each worker handles exactly one type,
there are no cross-worker collisions and no synchronization is needed.

However, this is O(types × total DAG nodes), and most combinations are empty. A `Belt` worker
walking an `ElectricalSystem` DAG will never find Belt nodes — it's wasted effort. We need a
way to statically determine "can a DAG rooted at type `TRoot` ever contain a child of type
`TTarget`?" so we can skip entire DAG walks.

## Approach A: Static Generic `IsOrCanReachChildOfType<TChild>()`

Introduce an `INode` interface that all node types implement:

```csharp
interface INode
{
    static abstract bool CanReachChildOfType<TChild>() where TChild : struct;
}
```

Each implementation would check its own type and recursively ask its child types:

```csharp
struct ParentNode : INode
{
    public Handle<ChildNode> Left;
    public Handle<OtherNode> Right;

    public static bool CanReachChildOfType<TChild>() where TChild : struct
    {
        if (typeof(TChild) == typeof(ParentNode)) return true;
        return ChildNode.CanReachChildOfType<TChild>()
            || OtherNode.CanReachChildOfType<TChild>();
    }
}
```

Since these are static generics using `typeof` checks, the JIT should collapse each
specialization to a constant `true` or `false`. **This must be verified with JIT baseline tests.**

### Cycle Problem

If `ParentNode` has a `Handle<ChildNode>` and `ChildNode` has a `Handle<ParentNode>`, the
recursive calls infinite-loop at runtime:

```
ParentNode.CanReachChildOfType<X>()
  → ChildNode.CanReachChildOfType<X>()
    → ParentNode.CanReachChildOfType<X>()  // infinite recursion
```

The JIT cannot break this cycle since each call is a real method invocation. Possible mitigations:
- Forbid cycles in the node type graph (may be too restrictive)
- Use a two-phase pattern where the first check is non-recursive ("am I this type?") and
  a separate compile-time mechanism handles transitive reachability

This makes Approach A fragile for real-world type graphs with mutual references.

## Approach B: Source Generator

Use a `[Node]` attribute on node types. A source generator analyzes all node types in the
assembly at compile time, builds the full type reference graph, performs reachability analysis
(DFS with visited set — handles cycles correctly), and emits a flat lookup:

```csharp
// Generated
static class NodeTypeReachability
{
    // Returns true if a DAG rooted at TRoot can contain a TTarget child.
    public static bool CanReach<TRoot, TTarget>()
        where TRoot : struct where TTarget : struct
    {
        if (typeof(TRoot) == typeof(ParentNode))
        {
            if (typeof(TTarget) == typeof(ParentNode)) return true;
            if (typeof(TTarget) == typeof(ChildNode)) return true;
            if (typeof(TTarget) == typeof(OtherNode)) return true;
            return false;
        }
        // ... other root types ...
        return false;
    }
}
```

### Advantages
- Handles cycles in the type graph correctly (compile-time graph analysis)
- No runtime recursion — flat `typeof` switch that the JIT collapses
- Can generate additional metadata (child field lists, enumerator implementations, etc.)
- Single source of truth: the `[Node]` attribute drives everything

### Considerations
- Requires a source generator project and build-time dependency
- All node types must be visible to the generator (same assembly or InternalsVisibleTo)
- The generator could also emit `INodeChildEnumerator` implementations, reducing boilerplate

## Decision

**Not yet decided.** Approach B (source generator) is likely the right long-term answer since it
handles cycles, eliminates manual boilerplate, and opens the door to generating enumerators and
other node infrastructure automatically. But it's a larger investment.

## Related

- `INodeChildEnumerator<TNode>` — the existing visitor pattern for enumerating node children
- `EnumerateRefChildren` / `VisitRef` — the ref-mutation path used during handle rewriting
- Coordinator merge pipeline (PR 4 in the job system plan)
