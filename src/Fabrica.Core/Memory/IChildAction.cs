namespace Fabrica.Core.Memory;

/// <summary>
/// Callback invoked by <see cref="IChildEnumerator{TNode,TContext}"/> for each child of a node.
/// Receives only the child's typed <see cref="Handle{T}"/> — the action struct captures whatever
/// context it needs (snapshot, refcount tables, collection buffers, etc.) at construction time.
///
/// GENERIC METHOD
///   <see cref="OnChild{TChild}"/> is generic so a single action struct can handle children of
///   any type. When called through a struct-constrained <c>ref TAction</c>, the JIT specializes
///   each <c>OnChild</c> instantiation — no virtual dispatch.
///
/// FULLY DECOUPLED
///   This interface has no knowledge of storage, refcounting, or any other subsystem. It is a
///   pure "for each child handle, do something" callback.
/// </summary>
internal interface IChildAction
{
    void OnChild<TChild>(Handle<TChild> child)
        where TChild : struct;
}
