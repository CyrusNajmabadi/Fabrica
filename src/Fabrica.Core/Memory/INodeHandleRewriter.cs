namespace Fabrica.Core.Memory;

/// <summary>
/// Visitor that rewrites child handles in-place during the coordinator's fixup pass. After the
/// parallel work phase, newly-created nodes contain local handles (encoded via
/// <see cref="TaggedHandle"/>). The coordinator walks each node's children and replaces local
/// handles with global ones using a remap table.
///
/// STRUCT GENERIC PATTERN
///   Same as <see cref="INodeVisitor"/>: the rewriter is a struct type parameter so the JIT
///   specializes per implementation, eliminating interface dispatch.
///
/// IMPLEMENTATION PATTERN
///   Use standalone <c>typeof</c> checks for cross-type nodes, same as <see cref="INodeVisitor"/>:
///   <code>
///   public void Rewrite&lt;TChild&gt;(ref Handle&lt;TChild&gt; handle) where TChild : struct
///   {
///       if (typeof(TChild) == typeof(MyNode))
///           RewriteMyNode(ref Unsafe.As&lt;Handle&lt;TChild&gt;, Handle&lt;MyNode&gt;&gt;(ref handle));
///   }
///   </code>
/// </summary>
internal interface INodeHandleRewriter
{
    void Rewrite<TChild>(ref Handle<TChild> handle) where TChild : struct;
}
