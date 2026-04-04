namespace Fabrica.Core.Memory;

/// <summary>
/// Enumerates ALL children of a node (same-type and cross-type), dispatching each child's
/// <see cref="Handle{T}"/> to an action callback. Implementations encode the structure of a
/// specific node type — which fields are child handles — and are reused across all operations
/// (increment, decrement, validate, collect, etc.).
///
/// TWO OVERLOADS
///   The context-free overload works with <see cref="IChildAction"/> for simple operations.
///   The context overload works with <see cref="IChildAction{TContext}"/> for operations that
///   need data (refcount tables, world state, etc.) passed from the caller through to the action.
///   Both overloads encode the same structural knowledge.
///
/// STRUCT GENERIC PATTERN
///   Both the enumerator and the action are struct type parameters. The JIT specializes each
///   combination into separate method bodies with no interface dispatch.
/// </summary>
internal interface IChildEnumerator<TNode> where TNode : struct
{
    void EnumerateChildren<TAction>(in TNode node, ref TAction action)
        where TAction : struct, IChildAction;

    void EnumerateChildren<TAction, TContext>(in TNode node, in TContext context, ref TAction action)
        where TAction : struct, IChildAction<TContext>;
}
