namespace Fabrica.Core.Memory.Nodes;

/// <summary>
/// Unified operations interface for a single node type: structural knowledge (which fields are
/// child handles) plus visitor callbacks (what to do per child during cascade). Implementations
/// encode the structure of a specific node type and are reused across all operations (increment,
/// decrement, validate, collect, rewrite, etc.).
///
/// Inherits <see cref="INodeVisitor"/> so a single struct satisfies the
/// <see cref="GlobalNodeStore{TNode,TNodeOps}"/> constraint with one type parameter.
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
public interface INodeOps<TNode> : INodeVisitor where TNode : struct
{
    void EnumerateChildren<TVisitor>(in TNode node, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor
        => throw new NotImplementedException();

    void EnumerateRefChildren<TVisitor>(ref TNode node, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor
        => throw new NotImplementedException();

    /// <summary>
    /// Increments the refcount for each valid child of <paramref name="node"/> in the appropriate
    /// store. Called during merge phase 2b after handles have been rewritten to global indices.
    /// Implementations dispatch to the correct <see cref="GlobalNodeStore{TNode,TNodeOps}"/> per
    /// child type — the same cross-type knowledge used by <see cref="EnumerateChildren{TVisitor}"/>.
    /// </summary>
    void IncrementChildRefCounts(in TNode node)
        => throw new NotImplementedException();
}
