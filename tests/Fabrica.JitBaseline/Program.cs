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

    VisitDifferentType(ref visitor, new Handle<OtherNode>(0));
    AssertRefCount(table, h0, 1, "Visit: after VisitDifferentType (should be unchanged)");

    VisitSameType(ref visitor, h0);
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
    EnumerateDecrement(ref enumerator, in arena[root], ref visitor);
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

    EnumerateMixedDecrement(ref enumerator, in mixedArena[parent], ref visitor);
    AssertRefCount(mixedTable, mixedChild, 0, "MixedEnumerate: same-type child (should be freed)");
    AssertRefCount(otherTable, otherChild, 1, "MixedEnumerate: cross-type child (should be unchanged)");
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
    Handle<TreeNode> child)
{
    visitor.Visit(child);
}

[MethodImpl(MethodImplOptions.NoInlining)]
static void VisitDifferentType(
    ref RefCountTable<TreeNode>.DecrementNodeRefCountVisitor<TreeHandler> visitor,
    Handle<OtherNode> child)
{
    visitor.Visit(child);
}

// End-to-end: enumerator walks TreeNode children (both same-type) through the decrement visitor.
[MethodImpl(MethodImplOptions.NoInlining)]
static void EnumerateDecrement(
    ref TreeChildEnumerator enumerator,
    in TreeNode node,
    ref RefCountTable<TreeNode>.DecrementNodeRefCountVisitor<TreeHandler> visitor)
{
    enumerator.EnumerateChildren(in node, ref visitor);
}

// End-to-end: enumerator walks MixedNode children (one same-type, one cross-type) through the
// decrement visitor. The cross-type child (OtherNode) should be dead-branch eliminated entirely.
[MethodImpl(MethodImplOptions.NoInlining)]
static void EnumerateMixedDecrement(
    ref MixedChildEnumerator enumerator,
    in MixedNode node,
    ref RefCountTable<MixedNode>.DecrementNodeRefCountVisitor<MixedHandler> visitor)
{
    enumerator.EnumerateChildren(in node, ref visitor);
}

// ── Node types ───────────────────────────────────────────────────────────

internal struct TreeNode
{
    public Handle<TreeNode> Left { get; set; }
    public Handle<TreeNode> Right { get; set; }
}

internal struct OtherNode
{
    public int Value { get; set; }
}

/// <summary>A node with children of two different types — exercises cross-type dead-branch elimination.</summary>
internal struct MixedNode
{
    public Handle<MixedNode> SameChild { get; set; }
    public Handle<OtherNode> CrossChild { get; set; }
}

// ── Enumerators ──────────────────────────────────────────────────────────

internal struct TreeChildEnumerator : INodeChildEnumerator<TreeNode>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void EnumerateChildren<TVisitor>(in TreeNode node, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor
    {
        if (node.Left.IsValid) visitor.Visit(node.Left);
        if (node.Right.IsValid) visitor.Visit(node.Right);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void EnumerateChildren<TVisitor, TContext>(in TreeNode node, in TContext context, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor<TContext>
    {
        if (node.Left.IsValid) visitor.Visit(node.Left, in context);
        if (node.Right.IsValid) visitor.Visit(node.Right, in context);
    }
}

internal struct MixedChildEnumerator : INodeChildEnumerator<MixedNode>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void EnumerateChildren<TVisitor>(in MixedNode node, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor
    {
        if (node.SameChild.IsValid) visitor.Visit(node.SameChild);
        if (node.CrossChild.IsValid) visitor.Visit(node.CrossChild);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void EnumerateChildren<TVisitor, TContext>(in MixedNode node, in TContext context, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor<TContext>
    {
        if (node.SameChild.IsValid) visitor.Visit(node.SameChild, in context);
        if (node.CrossChild.IsValid) visitor.Visit(node.CrossChild, in context);
    }
}

// ── Handlers ─────────────────────────────────────────────────────────────

internal struct TreeHandler(UnsafeSlabArena<TreeNode> arena, TreeChildEnumerator enumerator)
    : RefCountTable<TreeNode>.IRefCountHandler
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void OnFreed(Handle<TreeNode> handle, RefCountTable<TreeNode> table)
    {
        ref readonly var node = ref arena[handle];
        var visitor = new RefCountTable<TreeNode>.DecrementNodeRefCountVisitor<TreeHandler>(table, this);
        enumerator.EnumerateChildren(in node, ref visitor);
        arena.Free(handle);
    }
}

internal struct MixedHandler(UnsafeSlabArena<MixedNode> arena, MixedChildEnumerator enumerator)
    : RefCountTable<MixedNode>.IRefCountHandler
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void OnFreed(Handle<MixedNode> handle, RefCountTable<MixedNode> table)
    {
        ref readonly var node = ref arena[handle];
        var visitor = new RefCountTable<MixedNode>.DecrementNodeRefCountVisitor<MixedHandler>(table, this);
        enumerator.EnumerateChildren(in node, ref visitor);
        arena.Free(handle);
    }
}
