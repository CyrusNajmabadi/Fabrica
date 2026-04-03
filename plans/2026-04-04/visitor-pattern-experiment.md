# Visitor Pattern Zero-Overhead Experiment

## Goal

Prove that the proposed visitor pattern — where `IChildAction.OnChild<TChild, TChildHandler>(...)` is a generic method on a struct-constrained interface — compiles down to the same code as hand-rolled direct calls. This is the key risk: .NET historically has had issues with generic interface method dispatch, but struct constraints should force JIT specialization.

## What We're Testing

The .NET JIT must do all of the following for zero overhead:

1. **Struct-constrained devirtualization**: `TAction : struct, IChildAction` must eliminate interface dispatch
2. **Generic method specialization**: `action.OnChild<ParentNode, ParentHandler>(...)` and `action.OnChild<ChildNode, ChildHandler>(...)` must each get their own JIT-compiled method body
3. **Inlining**: the entire chain (EnumerateChildren -> OnChild -> store.Increment/Decrement) should collapse into flat code

## Interfaces Defined

```csharp
internal interface IChildEnumerator<TNode, TContext> where TNode : struct
{
    void EnumerateChildren<TAction>(in TNode node, in TContext context, ref TAction action)
        where TAction : struct, IChildAction;
}

internal interface IChildAction
{
    void OnChild<TChild, TChildHandler>(
        Handle<TChild> child,
        NodeStore<TChild, TChildHandler> store)
        where TChild : struct
        where TChildHandler : struct, RefCountTable<TChild>.IRefCountHandler;
}
```

## Benchmark Setup

- Cross-type world: `ParentNode` (with `Handle<ChildNode>` field) + `ChildNode`
- Three benchmarks at N = 1K, 10K, 100K:
  - `IncrementChildren_Direct` vs `IncrementChildren_Visitor`
  - `CascadeDecrement_Direct` vs `CascadeDecrement_Visitor`
- Direct = hand-rolled inline code (current pattern)
- Visitor = enumerator struct + action struct via the new interfaces

## Outcome

**Zero overhead confirmed.** At N=10K and N=100K (the meaningful sizes), the visitor
pattern is indistinguishable from hand-rolled code in both the increment path and
the cascade-decrement path. Memory allocation is identical (zero extra allocations).

Full results: `benchmarks/results/2026-04-04/visitor-experiment/comparison.md`

## Decision

Proceed with full visitor pattern integration:
1. Replace hand-rolled `IRefCountHandler` implementations with visitor-composed versions
2. Update `DagValidator` to use the new `IChildEnumerator<TNode, TContext>` (seeing all children)
3. Consider removing `TChildHandler` from `NodeStore` to simplify `IChildAction.OnChild` signature
