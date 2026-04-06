namespace Fabrica.Core.Memory;

/// <summary>
/// Visitor callback invoked by <see cref="INodeChildEnumerator{TNode}"/> for each child of a node.
/// Receives only the child's typed <see cref="Handle{T}"/> by reference — the visitor struct captures
/// whatever context it needs at construction time. Ref semantics allow read-only traversal and
/// in-place handle mutation (e.g. local-to-global fixup) through the same API.
///
/// For visitors that need context passed through from the caller, see <see cref="INodeVisitor{TContext}"/>.
///
/// CONTRACT
///   Enumerators must only call <see cref="Visit{TChild}"/> for child slots that are not the None
///   sentinel (<see cref="Handle{T}.None"/>, i.e. <c>Index == -1</c>). For tagged local handles,
///   indices are negative but not -1. Visitors may assert a non-None handle in debug builds but
///   should not re-check in release builds.
///
/// IMPLEMENTATION PATTERN
///   Implementations should dispatch on the child type using standalone <c>typeof</c> checks and
///   delegate to a typed helper method. This is the canonical pattern for JIT dead-branch elimination:
///   <code>
///   public void Visit&lt;TChild&gt;(ref Handle&lt;TChild&gt; child) where TChild : struct
///   {
///       if (typeof(TChild) == typeof(MyNode))
///           VisitMyNode(ref Unsafe.As&lt;Handle&lt;TChild&gt;, Handle&lt;MyNode&gt;&gt;(ref child));
///   }
///
///   private void VisitMyNode(ref Handle&lt;MyNode&gt; child)
///   {
///       Debug.Assert(child.Index != -1);
///       table.Decrement(child, handler);
///   }
///   </code>
///   Keep the <c>typeof</c> comparison as a standalone if-statement — do not combine it with
///   other conditions via <c>&amp;&amp;</c> — to match the pattern the JIT recognizes for
///   reliable dead-branch elimination.
/// </summary>
internal interface INodeVisitor
{
    void Visit<TChild>(ref Handle<TChild> child)
        where TChild : struct;
}

/// <summary>
/// Visitor callback invoked by <see cref="INodeChildEnumerator{TNode}"/> for each child of a node.
/// Receives the child's typed <see cref="Handle{T}"/> by reference and a strongly-typed context that
/// flows from the top-level operation through the enumerator. The context type is determined by the
/// caller — it can carry refcount tables, world state, or any operation-specific data.
///
/// For visitors that don't need context, see <see cref="INodeVisitor"/>.
///
/// IMPLEMENTATION PATTERN
///   Same pattern as <see cref="INodeVisitor"/>: use standalone <c>typeof</c> checks and typed
///   helper methods. The context is available to both the dispatch method and the typed helper.
/// </summary>
internal interface INodeVisitor<TContext>
{
    void Visit<TChild>(ref Handle<TChild> child, in TContext context)
        where TChild : struct;
}
