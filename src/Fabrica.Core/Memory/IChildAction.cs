namespace Fabrica.Core.Memory;

/// <summary>
/// Callback invoked by <see cref="IChildEnumerator{TNode,TContext}"/> for each child of a node.
/// The enumerator provides the child's typed <see cref="Handle{T}"/> and the
/// <see cref="RefCountTable{T}"/> it belongs to.
///
/// GENERIC METHOD
///   <see cref="OnChild{TChild}"/> is generic so a single action struct can handle children of
///   any type. When called through a struct-constrained <c>ref TAction</c>, the JIT specializes
///   each <c>OnChild</c> instantiation — no virtual dispatch.
///
/// DECOUPLED FROM REFCOUNT HANDLER
///   This interface has no knowledge of <see cref="RefCountTable{T}.IRefCountHandler"/>. Actions
///   operate on handles and refcount tables only — increment, validate, collect, etc. Cascade-free
///   decrement is a separate concern handled by the handler composition.
/// </summary>
internal interface IChildAction
{
    void OnChild<TChild>(Handle<TChild> child, RefCountTable<TChild> refCounts)
        where TChild : struct;
}
