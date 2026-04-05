namespace Fabrica.Core.Memory;

/// <summary>
/// Enumerates ALL children of a node (same-type and cross-type), dispatching each child's
/// <see cref="Handle{T}"/> to a visitor callback. Implementations encode the structure of a
/// specific node type — which fields are child handles — and are reused across all operations
/// (increment, decrement, validate, collect, etc.).
///
/// THREE METHOD FAMILIES
///   The context-free overload works with <see cref="INodeVisitor"/> for simple operations.
///   The context overload works with <see cref="INodeVisitor{TContext}"/> for operations that
///   need data (refcount tables, world state, etc.) passed from the caller through to the visitor.
///   The rewrite overload works with <see cref="INodeHandleRewriter"/> for in-place handle
///   mutation during the coordinator's fixup pass (local-to-global handle translation).
///   All three encode the same structural knowledge.
///
/// STRUCT GENERIC PATTERN
///   Both the enumerator and the visitor/rewriter are struct type parameters. The JIT specializes
///   each combination into separate method bodies with no interface dispatch.
/// </summary>
internal interface INodeChildEnumerator<TNode> where TNode : struct
{
    void EnumerateChildren<TVisitor>(in TNode node, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor;

    void EnumerateChildren<TVisitor, TContext>(in TNode node, in TContext context, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor<TContext>;

    /// <summary>
    /// Rewrites child handles in-place. Takes <c>ref TNode</c> (not <c>in</c>) so the rewriter
    /// can mutate the node's handle fields. Used by the coordinator during the fixup pass to
    /// translate local handles to global ones.
    /// </summary>
    void RewriteChildren<TRewriter>(ref TNode node, ref TRewriter rewriter)
        where TRewriter : struct, INodeHandleRewriter;
}
