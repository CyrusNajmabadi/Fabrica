namespace Fabrica.Core.Memory;

/// <summary>
/// Callback invoked by <see cref="IChildEnumerator{TNode,TContext}"/> for each child of a node.
/// The enumerator provides the child's typed <see cref="Handle{T}"/> and the
/// <see cref="NodeStore{TNode,THandler}"/> it belongs to, so the action can operate on the correct
/// arena and refcount table without knowing the world layout.
///
/// GENERIC METHOD
///   <see cref="OnChild{TChild,TChildHandler}"/> is generic so a single action struct can handle
///   children of any type. When called through a struct-constrained <c>ref TAction</c>, the JIT
///   specializes each <c>OnChild</c> instantiation — no virtual dispatch.
/// </summary>
internal interface IChildAction
{
    void OnChild<TChild, TChildHandler>(
        Handle<TChild> child,
        NodeStore<TChild, TChildHandler> store)
        where TChild : struct
        where TChildHandler : struct, RefCountTable<TChild>.IRefCountHandler;
}
