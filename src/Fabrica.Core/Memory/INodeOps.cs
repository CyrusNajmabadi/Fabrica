namespace Fabrica.Core.Memory;

/// <summary>
/// Unified operations interface for a single node type: structural knowledge (which fields are
/// child handles) plus visitor callbacks (what to do per child during cascade). Implementations
/// encode the structure of a specific node type and are reused across all operations (increment,
/// decrement, validate, collect, rewrite, etc.).
///
/// Inherits <see cref="INodeVisitor"/> so a single struct satisfies the
/// <see cref="NodeStore{TNode,TNodeOps}"/> constraint with one type parameter.
///
/// TWO METHOD FAMILIES
///   The read-only path: <see cref="EnumerateChildren{TVisitor}(in TNode, ref TVisitor)"/> with
///   <see cref="INodeVisitor.Visit{T}"/>.
///   The ref-mutation path: <see cref="EnumerateRefChildren{TVisitor}(ref TNode, ref TVisitor)"/>
///   with <see cref="INodeVisitor.VisitRef{T}"/>.
///
/// STRUCT GENERIC PATTERN
///   Both the node ops and the visitor are struct type parameters. The JIT specializes
///   each combination into separate method bodies with no interface dispatch.
/// </summary>
internal interface INodeOps<TNode> : INodeVisitor where TNode : struct
{
    void EnumerateChildren<TVisitor>(in TNode node, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor
        => throw new NotImplementedException();

    void EnumerateRefChildren<TVisitor>(ref TNode node, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor
        => throw new NotImplementedException();
}
