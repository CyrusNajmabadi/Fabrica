namespace Fabrica.Core.Memory;

/// <summary>
/// Enumerates ALL children of a node (same-type and cross-type), dispatching each to an
/// <see cref="IChildAction"/> callback. Implementations encode the structure of a specific node
/// type — which fields are child handles and which <see cref="NodeStore{TNode,THandler}"/> each
/// belongs to. The action is pluggable: increment, decrement, collect, validate, etc.
///
/// STRUCT GENERIC PATTERN
///   Both the enumerator and the action are struct type parameters. The JIT specializes each
///   combination (e.g., <c>EnumerateChildren&lt;IncrementAction&gt;</c> vs
///   <c>EnumerateChildren&lt;DecrementAction&gt;</c>) into separate method bodies with no interface
///   dispatch.
/// </summary>
internal interface IChildEnumerator<TNode, TContext> where TNode : struct
{
    void EnumerateChildren<TAction>(in TNode node, in TContext context, ref TAction action)
        where TAction : struct, IChildAction;
}
