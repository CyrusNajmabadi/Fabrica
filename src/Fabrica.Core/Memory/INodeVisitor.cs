namespace Fabrica.Core.Memory;

/// <summary>
/// Visitor callback invoked by <see cref="INodeChildEnumerator{TNode}"/> for each child of a node.
/// Receives only the child's typed <see cref="Handle{T}"/> — the visitor struct captures whatever
/// context it needs at construction time.
///
/// For visitors that need context passed through from the caller, see <see cref="INodeVisitor{TContext}"/>.
///
/// Two parallel paths exist: <see cref="Visit{TChild}"/> for read-only traversal (the child handle is
/// passed by value), and <see cref="VisitRef{TChild}"/> for in-place mutation (the handle is passed
/// by reference so the visitor can rewrite it). Enumerators choose the path via
/// <see cref="INodeChildEnumerator{TNode}.EnumerateChildren{TVisitor}(in TNode, ref TVisitor)"/> vs.
/// <see cref="INodeChildEnumerator{TNode}.EnumerateRefChildren{TVisitor}(ref TNode, ref TVisitor)"/>.
///
/// CONTRACT
///   Enumerators must only call <see cref="Visit{TChild}"/> with valid handles (i.e.,
///   <see cref="Handle{T}.IsValid"/> is true). Visitors may assert this in debug builds but
///   should not re-check in release builds.
///
/// IMPLEMENTATION PATTERN
///   Implementations should dispatch on the child type using standalone <c>typeof</c> checks and
///   delegate to a typed helper method. This is the canonical pattern for JIT dead-branch elimination:
///   <code>
///   public void Visit&lt;TChild&gt;(Handle&lt;TChild&gt; child) where TChild : struct
///   {
///       if (typeof(TChild) == typeof(MyNode))
///           VisitMyNode(Unsafe.As&lt;Handle&lt;TChild&gt;, Handle&lt;MyNode&gt;&gt;(ref child));
///   }
///
///   private void VisitMyNode(Handle&lt;MyNode&gt; child)
///   {
///       Debug.Assert(child.IsValid);
///       table.Decrement(child, handler);
///   }
///   </code>
///   Keep the <c>typeof</c> comparison as a standalone if-statement — do not combine it with
///   other conditions via <c>&amp;&amp;</c> — to match the pattern the JIT recognizes for
///   reliable dead-branch elimination.
/// </summary>
internal interface INodeVisitor
{
    void Visit<TChild>(Handle<TChild> child)
        where TChild : struct;

    void VisitRef<TChild>(ref Handle<TChild> child)
        where TChild : struct
        => throw new NotSupportedException();
}

/// <summary>
/// Visitor callback invoked by <see cref="INodeChildEnumerator{TNode}"/> for each child of a node.
/// Receives the child's typed <see cref="Handle{T}"/> and a strongly-typed context that flows
/// from the top-level operation through the enumerator. The context type is determined by the
/// caller — it can carry refcount tables, world state, or any operation-specific data.
///
/// For visitors that don't need context, see <see cref="INodeVisitor"/>.
///
/// As with <see cref="INodeVisitor"/>, there are read (<see cref="Visit{TChild}"/>) and ref-mutation
/// (<see cref="VisitRef{TChild}"/>) paths paired with the enumerator's
/// <see cref="INodeChildEnumerator{TNode}.EnumerateChildren{TVisitor, TContext}"/> vs.
/// <see cref="INodeChildEnumerator{TNode}.EnumerateRefChildren{TVisitor, TContext}"/> overloads.
///
/// IMPLEMENTATION PATTERN
///   Same pattern as <see cref="INodeVisitor"/>: use standalone <c>typeof</c> checks and typed
///   helper methods. The context is available to both the dispatch method and the typed helper.
/// </summary>
internal interface INodeVisitor<TContext>
{
    void Visit<TChild>(Handle<TChild> child, in TContext context)
        where TChild : struct;

    void VisitRef<TChild>(ref Handle<TChild> child, in TContext context)
        where TChild : struct
        => throw new NotSupportedException();
}
