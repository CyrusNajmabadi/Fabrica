namespace Fabrica.Core.Memory;

/// <summary>
/// Callback invoked by <see cref="IChildEnumerator{TNode}"/> for each child of a node.
/// Receives only the child's typed <see cref="Handle{T}"/> — the action struct captures whatever
/// context it needs at construction time.
///
/// For actions that need context passed through from the caller, see <see cref="IChildAction{TContext}"/>.
///
/// CONTRACT
///   Enumerators must only call <see cref="OnChild{TChild}"/> with valid handles (i.e.,
///   <see cref="Handle{T}.IsValid"/> is true). Actions may assert this in debug builds but
///   should not re-check in release builds.
///
/// IMPLEMENTATION PATTERN
///   Implementations should dispatch on the child type using standalone <c>typeof</c> checks and
///   delegate to a typed helper method. This is the canonical pattern for JIT dead-branch elimination:
///   <code>
///   public void OnChild&lt;TChild&gt;(Handle&lt;TChild&gt; child) where TChild : struct
///   {
///       if (typeof(TChild) == typeof(MyNode))
///           OnMyNodeChild(Unsafe.As&lt;Handle&lt;TChild&gt;, Handle&lt;MyNode&gt;&gt;(ref child));
///   }
///
///   private void OnMyNodeChild(Handle&lt;MyNode&gt; child)
///   {
///       Debug.Assert(child.IsValid);
///       table.Decrement(child, handler);
///   }
///   </code>
///   Keep the <c>typeof</c> comparison as a standalone if-statement — do not combine it with
///   other conditions via <c>&amp;&amp;</c> — to match the pattern the JIT recognizes for
///   reliable dead-branch elimination.
/// </summary>
internal interface IChildAction
{
    void OnChild<TChild>(Handle<TChild> child)
        where TChild : struct;
}

/// <summary>
/// Callback invoked by <see cref="IChildEnumerator{TNode}"/> for each child of a node.
/// Receives the child's typed <see cref="Handle{T}"/> and a strongly-typed context that flows
/// from the top-level operation through the enumerator. The context type is determined by the
/// caller — it can carry refcount tables, world state, or any operation-specific data.
///
/// For actions that don't need context, see <see cref="IChildAction"/>.
///
/// IMPLEMENTATION PATTERN
///   Same pattern as <see cref="IChildAction"/>: use standalone <c>typeof</c> checks and typed
///   helper methods. The context is available to both the dispatch method and the typed helper.
/// </summary>
internal interface IChildAction<TContext>
{
    void OnChild<TChild>(Handle<TChild> child, in TContext context)
        where TChild : struct;
}
