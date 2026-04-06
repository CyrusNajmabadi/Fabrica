namespace Fabrica.Core.Memory;

/// <summary>
/// Enumerates ALL children of a node (same-type and cross-type), dispatching each child's
/// <see cref="Handle{T}"/> to a visitor callback. Implementations encode the structure of a
/// specific node type — which fields are child handles — and are reused across all operations
/// (increment, decrement, validate, collect, in-place handle fixup, etc.).
///
/// TWO METHOD FAMILIES
///   The context-free overload works with <see cref="INodeVisitor"/> for simple operations.
///   The context overload works with <see cref="INodeVisitor{TContext}"/> for operations that
///   need data (refcount tables, world state, etc.) passed from the caller through to the visitor.
///   Both take <c>ref TNode</c> so the same traversal can update child handles in-place when needed.
///
/// STRUCT GENERIC PATTERN
///   Both the enumerator and the visitor are struct type parameters. The JIT specializes each
///   combination into separate method bodies with no interface dispatch.
/// </summary>
internal interface INodeChildEnumerator<TNode> where TNode : struct
{
    void EnumerateChildren<TVisitor>(ref TNode node, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor;

    void EnumerateChildren<TVisitor, TContext>(ref TNode node, in TContext context, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor<TContext>;
}
