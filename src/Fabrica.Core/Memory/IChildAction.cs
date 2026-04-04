namespace Fabrica.Core.Memory;

/// <summary>
/// Callback invoked by <see cref="IChildEnumerator{TNode}"/> for each child of a node.
/// Receives only the child's typed <see cref="Handle{T}"/> — the action struct captures whatever
/// context it needs at construction time.
///
/// For actions that need context passed through from the caller, see <see cref="IChildAction{TContext}"/>.
/// </summary>
internal interface IChildAction
{
    void OnChild<TChild>(Handle<TChild> child)
        where TChild : struct;
}

/// <summary>
/// Callback invoked by <see cref="IChildEnumerator{TNode}"/> for each child of a node.
/// Receives the child's typed <see cref="Handle{T}"/> and a strongly-typed context that flows
/// from the top-level operation through the enumerator. The context type is determined by the
/// caller — it can carry refcount tables, world state, or any operation-specific data.
///
/// For actions that don't need context, see <see cref="IChildAction"/>.
/// </summary>
internal interface IChildAction<TContext>
{
    void OnChild<TChild>(Handle<TChild> child, in TContext context)
        where TChild : struct;
}
