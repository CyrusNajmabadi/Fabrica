namespace Fabrica.Core.Memory.Nodes;

/// <summary>
/// Visitor callback invoked by <see cref="INodeOps{TNode}"/> for each child of a node.
/// Receives only the child's typed <see cref="Handle{T}"/> — the visitor struct captures whatever
/// context it needs at construction time.
///
/// Two parallel paths exist: <see cref="Visit{T}"/> for read-only traversal (the child handle is
/// passed by value), and <see cref="VisitRef{T}"/> for in-place mutation (the handle is passed
/// by reference so the visitor can rewrite it). Enumerators choose the path via
/// <see cref="INodeOps{TNode}.EnumerateChildren{TVisitor}(in TNode, ref TVisitor)"/> vs.
/// <see cref="INodeOps{TNode}.EnumerateRefChildren{TVisitor}(ref TNode, ref TVisitor)"/>.
///
/// CONTRACT
///   Enumerators must only call <see cref="Visit{T}"/> with valid handles (i.e.,
///   <see cref="Handle{T}.IsValid"/> is true). Visitors may assert this in debug builds but
///   should not re-check in release builds.
///
/// IMPLEMENTATION PATTERN
///   Implementations should dispatch on the child type using standalone <c>typeof</c> checks and
///   delegate to a typed helper method. This is the canonical pattern for JIT dead-branch elimination:
///   <code>
///   public void Visit&lt;T&gt;(Handle&lt;T&gt; handle) where T : struct
///   {
///       if (typeof(T) == typeof(MyNode))
///           VisitMyNode(Unsafe.As&lt;Handle&lt;T&gt;, Handle&lt;MyNode&gt;&gt;(ref handle));
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
public interface INodeVisitor
{
    void Visit<T>(Handle<T> handle)
        where T : struct
        => throw new NotImplementedException();

    void VisitRef<T>(ref Handle<T> handle)
        where T : struct
        => throw new NotImplementedException();
}
