namespace Fabrica.Core.Memory;

/// <summary>
/// Enumerates ALL children of a node (same-type and cross-type), dispatching each child's
/// <see cref="Handle{T}"/> to a visitor callback. Implementations encode the structure of a
/// specific node type — which fields are child handles — and are reused across all operations
/// (increment, decrement, validate, collect, etc.).
///
/// FOUR METHOD FAMILIES
///   The read-only pair: <see cref="EnumerateChildren{TVisitor}(in TNode, ref TVisitor)"/> with
///   <see cref="INodeVisitor"/> / <see cref="INodeVisitor.Visit{TChild}"/>, and the context overload
///   <see cref="EnumerateChildren{TVisitor, TContext}(in TNode, in TContext, ref TVisitor)"/> with
///   <see cref="INodeVisitor{TContext}"/> / <see cref="INodeVisitor{TContext}.Visit{TChild}"/>.
///   The ref-mutation pair: <see cref="EnumerateRefChildren{TVisitor}(ref TNode, ref TVisitor)"/> with
///   <see cref="INodeVisitor.VisitRef{TChild}"/>, and the context overload
///   <see cref="EnumerateRefChildren{TVisitor, TContext}(ref TNode, in TContext, ref TVisitor)"/> with
///   <see cref="INodeVisitor{TContext}.VisitRef{TChild}"/>. All four encode the same structural knowledge.
///
/// STRUCT GENERIC PATTERN
///   Both the enumerator and the visitor are struct type parameters. The JIT specializes
///   each combination into separate method bodies with no interface dispatch.
/// </summary>
internal interface INodeChildEnumerator<TNode> where TNode : struct
{
    void EnumerateChildren<TVisitor>(in TNode node, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor;

    void EnumerateChildren<TVisitor, TContext>(in TNode node, in TContext context, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor<TContext>;

    void EnumerateRefChildren<TVisitor>(ref TNode node, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor
        => throw new NotSupportedException();

    void EnumerateRefChildren<TVisitor, TContext>(ref TNode node, in TContext context, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor<TContext>
        => throw new NotSupportedException();
}
