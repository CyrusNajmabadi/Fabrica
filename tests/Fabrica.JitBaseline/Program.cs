using System.Runtime.CompilerServices;
using Fabrica.Core.Memory;

// Each wrapper is NoInlining so DOTNET_JitDisasm shows a clean, isolated listing per scenario.
// The JIT still inlines *into* these wrappers (AggressiveInlining on Visit), so the generated
// code reveals whether dead-branch elimination and struct devirtualization actually happened.
//
// RUNTIME CORRECTNESS: The app also verifies the visitor produces the correct side effects
// (refcount changes), not just correct-looking ASM. A non-zero exit code means a check failed.

// ── Scenario 1: Isolated Visit (same-type vs cross-type) ─────────────
{
    var arena = new UnsafeSlabArena<TreeNode>();
    var table = new RefCountTable<TreeNode>();
    table.EnsureCapacity(16);

    var h0 = arena.Allocate();
    arena[h0] = new TreeNode { Left = new Handle<TreeNode>(-1), Right = new Handle<TreeNode>(-1) };
    table.Increment(h0);
    AssertRefCount(table, h0, 1, "Visit: after Increment");

    var handler = new TreeHandler(arena, default);
    var visitor = new RefCountTable<TreeNode>.DecrementNodeRefCountVisitor<TreeHandler>(table, handler);

    var otherH = new Handle<OtherNode>(0);
    VisitDifferentType(ref visitor, ref otherH);
    AssertRefCount(table, h0, 1, "Visit: after VisitDifferentType (should be unchanged)");

    var visitH0 = h0;
    VisitSameType(ref visitor, ref visitH0);
    AssertRefCount(table, h0, 0, "Visit: after VisitSameType (should be freed)");
}

// ── Scenario 2: Full pipeline — enumerator + visitor (same-type children) ───
{
    var arena = new UnsafeSlabArena<TreeNode>();
    var table = new RefCountTable<TreeNode>();
    table.EnsureCapacity(16);

    // Build: root → left, root → right  (a 3-node tree)
    var left = arena.Allocate();
    arena[left] = new TreeNode { Left = new Handle<TreeNode>(-1), Right = new Handle<TreeNode>(-1) };
    table.Increment(left); // held by root

    var right = arena.Allocate();
    arena[right] = new TreeNode { Left = new Handle<TreeNode>(-1), Right = new Handle<TreeNode>(-1) };
    table.Increment(right); // held by root

    var root = arena.Allocate();
    arena[root] = new TreeNode { Left = left, Right = right };
    table.Increment(root); // the external hold

    var enumerator = new TreeChildEnumerator();
    var handler = new TreeHandler(arena, enumerator);
    var visitor = new RefCountTable<TreeNode>.DecrementNodeRefCountVisitor<TreeHandler>(table, handler);

    // EnumerateDecrement walks root's children and decrements each.
    // This exercises: enumerator.EnumerateChildren → visitor.Visit → table.Decrement.
    EnumerateDecrement(ref enumerator, ref arena[root], ref visitor);
    AssertRefCount(table, left, 0, "EnumerateDecrement: left child after enumerate (should be freed)");
    AssertRefCount(table, right, 0, "EnumerateDecrement: right child after enumerate (should be freed)");
    AssertRefCount(table, root, 1, "EnumerateDecrement: root refcount unchanged");
}

// ── Scenario 3: Full pipeline — enumerator + visitor (mixed-type children) ──
{
    var mixedArena = new UnsafeSlabArena<MixedNode>();
    var mixedTable = new RefCountTable<MixedNode>();
    mixedTable.EnsureCapacity(16);

    var otherArena = new UnsafeSlabArena<OtherNode>();
    var otherTable = new RefCountTable<OtherNode>();
    otherTable.EnsureCapacity(16);

    // The mixed child is a MixedNode (same-type), the other child is OtherNode (cross-type).
    var mixedChild = mixedArena.Allocate();
    mixedArena[mixedChild] = new MixedNode { SameChild = new Handle<MixedNode>(-1), CrossChild = new Handle<OtherNode>(-1) };
    mixedTable.Increment(mixedChild);

    var otherChild = otherArena.Allocate();
    otherArena[otherChild] = new OtherNode { Value = 42 };
    otherTable.Increment(otherChild);

    var parent = mixedArena.Allocate();
    mixedArena[parent] = new MixedNode { SameChild = mixedChild, CrossChild = otherChild };
    mixedTable.Increment(parent);

    // DecrementNodeRefCountVisitor is only for MixedNode — it should decrement mixedChild
    // but completely ignore otherChild (the typeof(OtherNode) branch is eliminated).
    var handler = new MixedHandler(mixedArena, default);
    var visitor = new RefCountTable<MixedNode>.DecrementNodeRefCountVisitor<MixedHandler>(mixedTable, handler);
    var enumerator = new MixedChildEnumerator();

    EnumerateMixedDecrement(ref enumerator, ref mixedArena[parent], ref visitor);
    AssertRefCount(mixedTable, mixedChild, 0, "MixedEnumerate: same-type child (should be freed)");
    AssertRefCount(otherTable, otherChild, 1, "MixedEnumerate: cross-type child (should be unchanged)");
}

// ── Scenario 4: Full pipeline — custom multi-type visitor (both typeof branches active) ──
{
    var parentArena = new UnsafeSlabArena<ParentNode>();
    var parentTable = new RefCountTable<ParentNode>();
    parentTable.EnsureCapacity(16);

    var childArena = new UnsafeSlabArena<ChildNode>();
    var childTable = new RefCountTable<ChildNode>();
    childTable.EnsureCapacity(16);

    // Create a ChildNode and a ParentNode child (both held by the root ParentNode).
    var childRef = childArena.Allocate();
    childArena[childRef] = new ChildNode { Value = 99 };
    childTable.Increment(childRef);

    var parentChild = parentArena.Allocate();
    parentArena[parentChild] = new ParentNode { ParentRef = new Handle<ParentNode>(-1), ChildRef = new Handle<ChildNode>(-1) };
    parentTable.Increment(parentChild);

    var root = parentArena.Allocate();
    parentArena[root] = new ParentNode { ParentRef = parentChild, ChildRef = childRef };
    parentTable.Increment(root);

    // ParentDecrementVisitor has two typeof branches: one for ParentNode, one for ChildNode.
    // Both should resolve and inline — no dead branches, no interface dispatch.
    var enumerator = new ParentChildEnumerator();
    var childHandler = new ChildHandler(childArena);
    var parentHandler = new ParentHandler(parentArena, enumerator, childTable, childHandler);
    var visitor = new ParentDecrementVisitor(parentTable, parentHandler, childTable, childHandler);

    EnumerateParentDecrement(ref enumerator, ref parentArena[root], ref visitor);
    AssertRefCount(parentTable, parentChild, 0, "ParentDecrement: parent-type child (should be freed)");
    AssertRefCount(childTable, childRef, 0, "ParentDecrement: child-type child (should be freed)");
    AssertRefCount(parentTable, root, 1, "ParentDecrement: root refcount unchanged");
}

return 0;

static void AssertRefCount<T>(RefCountTable<T> table, Handle<T> handle, int expected, string context) where T : struct
{
    var actual = table.GetCount(handle);
    if (actual != expected)
    {
        Console.Error.WriteLine($"FAIL: refcount for {handle} {context}: expected {expected}, got {actual}");
        Environment.Exit(1);
    }
}

// Wrappers — these are what DOTNET_JitDisasm will dump.

[MethodImpl(MethodImplOptions.NoInlining)]
static void VisitSameType(
    ref RefCountTable<TreeNode>.DecrementNodeRefCountVisitor<TreeHandler> visitor,
    ref Handle<TreeNode> child)
{
    visitor.Visit(ref child);
}

[MethodImpl(MethodImplOptions.NoInlining)]
static void VisitDifferentType(
    ref RefCountTable<TreeNode>.DecrementNodeRefCountVisitor<TreeHandler> visitor,
    ref Handle<OtherNode> child)
{
    visitor.Visit(ref child);
}

// End-to-end: enumerator walks TreeNode children (both same-type) through the decrement visitor.
[MethodImpl(MethodImplOptions.NoInlining)]
static void EnumerateDecrement(
    ref TreeChildEnumerator enumerator,
    ref TreeNode node,
    ref RefCountTable<TreeNode>.DecrementNodeRefCountVisitor<TreeHandler> visitor)
{
    enumerator.EnumerateChildren(ref node, ref visitor);
}

// End-to-end: enumerator walks MixedNode children (one same-type, one cross-type) through the
// decrement visitor. The cross-type child (OtherNode) should be dead-branch eliminated entirely.
[MethodImpl(MethodImplOptions.NoInlining)]
static void EnumerateMixedDecrement(
    ref MixedChildEnumerator enumerator,
    ref MixedNode node,
    ref RefCountTable<MixedNode>.DecrementNodeRefCountVisitor<MixedHandler> visitor)
{
    enumerator.EnumerateChildren(ref node, ref visitor);
}

// End-to-end: ParentChildEnumerator walks ParentNode children (Handle<ParentNode> + Handle<ChildNode>)
// through a custom ParentDecrementVisitor that has typeof branches for BOTH types.
// Both branches should resolve to direct Decrement calls — no dead branches, both active.
[MethodImpl(MethodImplOptions.NoInlining)]
static void EnumerateParentDecrement(
    ref ParentChildEnumerator enumerator,
    ref ParentNode node,
    ref ParentDecrementVisitor visitor)
{
    enumerator.EnumerateChildren(ref node, ref visitor);
}

// ── Node types ───────────────────────────────────────────────────────────

internal struct TreeNode
{
    public Handle<TreeNode> Left;
    public Handle<TreeNode> Right;
}

internal struct OtherNode
{
    public int Value { get; set; }
}

/// <summary>A node with children of two different types — used with DecrementNodeRefCountVisitor
/// which only handles one type (the cross-type child is dead-branch eliminated).</summary>
internal struct MixedNode
{
    public Handle<MixedNode> SameChild;
    public Handle<OtherNode> CrossChild;
}

/// <summary>A node with children of two different types — used with ParentDecrementVisitor
/// which has active typeof branches for BOTH types (neither is eliminated).</summary>
internal struct ParentNode
{
    public Handle<ParentNode> ParentRef;
    public Handle<ChildNode> ChildRef;
}

internal struct ChildNode
{
    public int Value { get; set; }
}

// ── Enumerators ──────────────────────────────────────────────────────────

internal struct TreeChildEnumerator : INodeChildEnumerator<TreeNode>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void EnumerateChildren<TVisitor>(ref TreeNode node, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor
    {
        if (node.Left.Index != -1) visitor.Visit(ref node.Left);
        if (node.Right.Index != -1) visitor.Visit(ref node.Right);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void EnumerateChildren<TVisitor, TContext>(ref TreeNode node, in TContext context, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor<TContext>
    {
        if (node.Left.Index != -1) visitor.Visit(ref node.Left, in context);
        if (node.Right.Index != -1) visitor.Visit(ref node.Right, in context);
    }
}

internal struct MixedChildEnumerator : INodeChildEnumerator<MixedNode>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void EnumerateChildren<TVisitor>(ref MixedNode node, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor
    {
        if (node.SameChild.Index != -1) visitor.Visit(ref node.SameChild);
        if (node.CrossChild.Index != -1) visitor.Visit(ref node.CrossChild);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void EnumerateChildren<TVisitor, TContext>(ref MixedNode node, in TContext context, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor<TContext>
    {
        if (node.SameChild.Index != -1) visitor.Visit(ref node.SameChild, in context);
        if (node.CrossChild.Index != -1) visitor.Visit(ref node.CrossChild, in context);
    }
}

internal struct ParentChildEnumerator : INodeChildEnumerator<ParentNode>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void EnumerateChildren<TVisitor>(ref ParentNode node, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor
    {
        if (node.ParentRef.Index != -1) visitor.Visit(ref node.ParentRef);
        if (node.ChildRef.Index != -1) visitor.Visit(ref node.ChildRef);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void EnumerateChildren<TVisitor, TContext>(ref ParentNode node, in TContext context, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor<TContext>
    {
        if (node.ParentRef.Index != -1) visitor.Visit(ref node.ParentRef, in context);
        if (node.ChildRef.Index != -1) visitor.Visit(ref node.ChildRef, in context);
    }
}

// ── Visitors ─────────────────────────────────────────────────────────────

/// <summary>
/// Custom visitor with two active typeof branches — one for ParentNode, one for ChildNode.
/// Unlike DecrementNodeRefCountVisitor (which only handles one type), both branches here
/// are live: the JIT should resolve each typeof check to the correct Decrement call with
/// no dead-branch elimination (both are reachable).
/// </summary>
internal struct ParentDecrementVisitor(
    RefCountTable<ParentNode> parentTable,
    ParentHandler parentHandler,
    RefCountTable<ChildNode> childTable,
    ChildHandler childHandler) : INodeVisitor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Visit<TChild>(ref Handle<TChild> child) where TChild : struct
    {
        if (typeof(TChild) == typeof(ParentNode))
            this.DecrementParent(Unsafe.As<Handle<TChild>, Handle<ParentNode>>(ref child));
        else if (typeof(TChild) == typeof(ChildNode))
            this.DecrementChild(Unsafe.As<Handle<TChild>, Handle<ChildNode>>(ref child));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly void DecrementParent(Handle<ParentNode> child) => parentTable.Decrement(child, parentHandler);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly void DecrementChild(Handle<ChildNode> child) => childTable.Decrement(child, childHandler);
}

// ── Handlers ─────────────────────────────────────────────────────────────

internal struct TreeHandler(UnsafeSlabArena<TreeNode> arena, TreeChildEnumerator enumerator)
    : RefCountTable<TreeNode>.IRefCountHandler
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void OnFreed(Handle<TreeNode> handle, RefCountTable<TreeNode> table)
    {
        ref var node = ref arena[handle];
        var visitor = new RefCountTable<TreeNode>.DecrementNodeRefCountVisitor<TreeHandler>(table, this);
        enumerator.EnumerateChildren(ref node, ref visitor);
        arena.Free(handle);
    }
}

internal struct MixedHandler(UnsafeSlabArena<MixedNode> arena, MixedChildEnumerator enumerator)
    : RefCountTable<MixedNode>.IRefCountHandler
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void OnFreed(Handle<MixedNode> handle, RefCountTable<MixedNode> table)
    {
        ref var node = ref arena[handle];
        var visitor = new RefCountTable<MixedNode>.DecrementNodeRefCountVisitor<MixedHandler>(table, this);
        enumerator.EnumerateChildren(ref node, ref visitor);
        arena.Free(handle);
    }
}

internal struct ParentHandler(UnsafeSlabArena<ParentNode> arena, ParentChildEnumerator enumerator,
    RefCountTable<ChildNode> childTable, ChildHandler childHandler)
    : RefCountTable<ParentNode>.IRefCountHandler
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void OnFreed(Handle<ParentNode> handle, RefCountTable<ParentNode> table)
    {
        ref var node = ref arena[handle];
        var visitor = new ParentDecrementVisitor(table, this, childTable, childHandler);
        enumerator.EnumerateChildren(ref node, ref visitor);
        arena.Free(handle);
    }
}

internal struct ChildHandler(UnsafeSlabArena<ChildNode> arena)
    : RefCountTable<ChildNode>.IRefCountHandler
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void OnFreed(Handle<ChildNode> handle, RefCountTable<ChildNode> table) => arena.Free(handle);
}
