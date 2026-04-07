using System.Runtime.CompilerServices;
using Fabrica.Core.Memory;
using Fabrica.Core.Memory.Nodes;

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

    var visitor = new TreeDecrementVisitor(table);

    VisitDifferentType(ref visitor, new Handle<OtherNode>(0));
    AssertRefCount(table, h0, 1, "Visit: after VisitDifferentType (should be unchanged)");

    VisitSameType(ref visitor, h0);
    AssertRefCount(table, h0, 0, "Visit: after VisitSameType (refcount should be zero)");
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
    var visitor = new TreeDecrementVisitor(table);

    // EnumerateDecrement walks root's children and decrements each.
    // This exercises: enumerator.EnumerateChildren → visitor.Visit → table.Decrement.
    EnumerateDecrement(ref enumerator, in arena[root], ref visitor);
    AssertRefCount(table, left, 0, "EnumerateDecrement: left child refcount after enumerate");
    AssertRefCount(table, right, 0, "EnumerateDecrement: right child refcount after enumerate");
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

    // MixedDecrementVisitor is only for MixedNode — it should decrement mixedChild
    // but completely ignore otherChild (the typeof(OtherNode) branch is eliminated).
    var visitor = new MixedDecrementVisitor(mixedTable);
    var enumerator = new MixedChildEnumerator();

    EnumerateMixedDecrement(ref enumerator, in mixedArena[parent], ref visitor);
    AssertRefCount(mixedTable, mixedChild, 0, "MixedEnumerate: same-type child refcount");
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
    childArena[childRef] = new ChildNode { LeftChild = new Handle<ChildNode>(-1), RightChild = new Handle<ChildNode>(-1), Value = 99 };
    childTable.Increment(childRef);

    var parentChild = parentArena.Allocate();
    parentArena[parentChild] = new ParentNode { ParentRef = new Handle<ParentNode>(-1), ChildRef = new Handle<ChildNode>(-1) };
    parentTable.Increment(parentChild);

    var root = parentArena.Allocate();
    parentArena[root] = new ParentNode { ParentRef = parentChild, ChildRef = childRef };
    parentTable.Increment(root);

    var enumerator = new ParentChildEnumerator();
    var visitor = new ParentDecrementVisitor(parentTable, childTable);

    EnumerateParentDecrement(ref enumerator, in parentArena[root], ref visitor);
    AssertRefCount(parentTable, parentChild, 0, "ParentDecrement: parent-type child refcount");
    AssertRefCount(childTable, childRef, 0, "ParentDecrement: child-type child refcount");
    AssertRefCount(parentTable, root, 1, "ParentDecrement: root refcount unchanged");
}

// ── Scenario 5: Multi-impl INodeOps — single struct for two node types ────
//
// MultiTypeOps implements INodeOps<ParentNode> and INodeOps<ChildNode> on one struct.
// The Visit<T> method handles both types via typeof checks; EnumerateChildren is per-type
// via explicit interface implementation. The JIT should specialize each EnumerateChildren
// call site independently, eliminating dead typeof branches just as with separate structs.
{
    var parentArena = new UnsafeSlabArena<ParentNode>();
    var parentTable = new RefCountTable<ParentNode>();
    parentTable.EnsureCapacity(16);

    var childArena = new UnsafeSlabArena<ChildNode>();
    var childTable = new RefCountTable<ChildNode>();
    childTable.EnsureCapacity(16);

    var childRef = childArena.Allocate();
    childArena[childRef] = new ChildNode { Value = 99 };
    childTable.Increment(childRef);

    var parentChild = parentArena.Allocate();
    parentArena[parentChild] = new ParentNode { ParentRef = new Handle<ParentNode>(-1), ChildRef = new Handle<ChildNode>(-1) };
    parentTable.Increment(parentChild);

    var root = parentArena.Allocate();
    parentArena[root] = new ParentNode { ParentRef = parentChild, ChildRef = childRef };
    parentTable.Increment(root);

    var ops = new MultiTypeOps(parentTable, childTable);

    EnumerateMultiOpsParent(ref ops, in parentArena[root], ref ops);
    AssertRefCount(parentTable, parentChild, 0, "MultiOps-Parent: parent-type child refcount");
    AssertRefCount(childTable, childRef, 0, "MultiOps-Parent: child-type child refcount");
    AssertRefCount(parentTable, root, 1, "MultiOps-Parent: root refcount unchanged");

    // Reset for the child-path test
    parentTable.Increment(parentChild);
    childTable.Increment(childRef);

    var childLeft = childArena.Allocate();
    childArena[childLeft] = new ChildNode { Value = 1 };
    childTable.Increment(childLeft);

    var childRight = childArena.Allocate();
    childArena[childRight] = new ChildNode { Value = 2 };
    childTable.Increment(childRight);

    var childParent = childArena.Allocate();
    childArena[childParent] = new ChildNode { Value = 0, LeftChild = childLeft, RightChild = childRight };
    childTable.Increment(childParent);

    EnumerateMultiOpsChild(ref ops, in childArena[childParent], ref ops);
    AssertRefCount(childTable, childLeft, 0, "MultiOps-Child: left child refcount");
    AssertRefCount(childTable, childRight, 0, "MultiOps-Child: right child refcount");
    AssertRefCount(childTable, childParent, 1, "MultiOps-Child: parent refcount unchanged");
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
    ref TreeDecrementVisitor visitor,
    Handle<TreeNode> child)
{
    visitor.Visit(child);
}

[MethodImpl(MethodImplOptions.NoInlining)]
static void VisitDifferentType(
    ref TreeDecrementVisitor visitor,
    Handle<OtherNode> child)
{
    visitor.Visit(child);
}

// End-to-end: enumerator walks TreeNode children (both same-type) through the decrement visitor.
[MethodImpl(MethodImplOptions.NoInlining)]
static void EnumerateDecrement(
    ref TreeChildEnumerator enumerator,
    in TreeNode node,
    ref TreeDecrementVisitor visitor)
{
    enumerator.EnumerateChildren(in node, ref visitor);
}

// End-to-end: enumerator walks MixedNode children (one same-type, one cross-type) through the
// decrement visitor. The cross-type child (OtherNode) should be dead-branch eliminated entirely.
[MethodImpl(MethodImplOptions.NoInlining)]
static void EnumerateMixedDecrement(
    ref MixedChildEnumerator enumerator,
    in MixedNode node,
    ref MixedDecrementVisitor visitor)
{
    enumerator.EnumerateChildren(in node, ref visitor);
}

// End-to-end: ParentChildEnumerator walks ParentNode children (Handle<ParentNode> + Handle<ChildNode>)
// through a custom ParentDecrementVisitor that has typeof branches for BOTH types.
// Both branches should resolve to direct Decrement calls — no dead branches, both active.
[MethodImpl(MethodImplOptions.NoInlining)]
static void EnumerateParentDecrement(
    ref ParentChildEnumerator enumerator,
    in ParentNode node,
    ref ParentDecrementVisitor visitor)
{
    enumerator.EnumerateChildren(in node, ref visitor);
}

// Multi-impl: exercises INodeOps<ParentNode>.EnumerateChildren on MultiTypeOps.
// The visitor is the same MultiTypeOps struct — both typeof branches should resolve.
[MethodImpl(MethodImplOptions.NoInlining)]
static void EnumerateMultiOpsParent(
    ref MultiTypeOps ops,
    in ParentNode node,
    ref MultiTypeOps visitor)
{
    ConstrainedEnumerate(ref ops, in node, ref visitor);
}

// Multi-impl: exercises INodeOps<ChildNode>.EnumerateChildren on MultiTypeOps.
// Only the ChildNode typeof branch should survive — ParentNode is dead-branch eliminated.
[MethodImpl(MethodImplOptions.NoInlining)]
static void EnumerateMultiOpsChild(
    ref MultiTypeOps ops,
    in ChildNode node,
    ref MultiTypeOps visitor)
{
    ConstrainedEnumerate(ref ops, in node, ref visitor);
}

// Constrained generic call — the JIT specializes TOps.EnumerateChildren for the concrete types
// without boxing, exactly as GlobalNodeStore.RunCascade calls through its TNodeOps constraint.
[MethodImpl(MethodImplOptions.AggressiveInlining)]
static void ConstrainedEnumerate<TOps, TNode, TVisitor>(ref TOps ops, in TNode node, ref TVisitor visitor)
    where TOps : struct, INodeOps<TNode>
    where TNode : struct
    where TVisitor : struct, INodeVisitor
{
    ops.EnumerateChildren(in node, ref visitor);
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

/// <summary>A node with children of two different types — used with MixedDecrementVisitor
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
    public Handle<ChildNode> LeftChild;
    public Handle<ChildNode> RightChild;
    public int Value;
}

// ── Enumerators ──────────────────────────────────────────────────────────

internal struct TreeChildEnumerator : INodeOps<TreeNode>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void EnumerateChildren<TVisitor>(in TreeNode node, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor
    {
        if (node.Left.IsValid) visitor.Visit(node.Left);
        if (node.Right.IsValid) visitor.Visit(node.Right);
    }
}

internal struct MixedChildEnumerator : INodeOps<MixedNode>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void EnumerateChildren<TVisitor>(in MixedNode node, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor
    {
        if (node.SameChild.IsValid) visitor.Visit(node.SameChild);
        if (node.CrossChild.IsValid) visitor.Visit(node.CrossChild);
    }
}

internal struct ParentChildEnumerator : INodeOps<ParentNode>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void EnumerateChildren<TVisitor>(in ParentNode node, ref TVisitor visitor)
        where TVisitor : struct, INodeVisitor
    {
        if (node.ParentRef.IsValid) visitor.Visit(node.ParentRef);
        if (node.ChildRef.IsValid) visitor.Visit(node.ChildRef);
    }
}

// ── Visitors ─────────────────────────────────────────────────────────────

internal struct TreeDecrementVisitor(RefCountTable<TreeNode> table) : INodeVisitor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Visit<T>(Handle<T> handle) where T : struct
    {
        if (typeof(T) == typeof(TreeNode))
        {
            var c = Unsafe.As<Handle<T>, Handle<TreeNode>>(ref handle);
            table.Decrement(c);
        }
    }
}

internal struct MixedDecrementVisitor(RefCountTable<MixedNode> table) : INodeVisitor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Visit<T>(Handle<T> handle) where T : struct
    {
        if (typeof(T) == typeof(MixedNode))
        {
            var c = Unsafe.As<Handle<T>, Handle<MixedNode>>(ref handle);
            table.Decrement(c);
        }
    }
}

/// <summary>
/// Custom visitor with two active typeof branches — one for ParentNode, one for ChildNode.
/// Unlike TreeDecrementVisitor / MixedDecrementVisitor (which only handle one type), both branches here
/// are live: the JIT should resolve each typeof check to the correct Decrement call with
/// no dead-branch elimination (both are reachable).
/// </summary>
internal struct ParentDecrementVisitor(
    RefCountTable<ParentNode> parentTable,
    RefCountTable<ChildNode> childTable) : INodeVisitor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Visit<T>(Handle<T> handle) where T : struct
    {
        if (typeof(T) == typeof(ParentNode))
            this.DecrementParent(Unsafe.As<Handle<T>, Handle<ParentNode>>(ref handle));
        else if (typeof(T) == typeof(ChildNode))
            this.DecrementChild(Unsafe.As<Handle<T>, Handle<ChildNode>>(ref handle));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly void DecrementParent(Handle<ParentNode> child) => parentTable.Decrement(child);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly void DecrementChild(Handle<ChildNode> child) => childTable.Decrement(child);
}

/// <summary>
/// Single struct implementing INodeOps for two node types. Combines enumerator and visitor
/// logic — the JIT must specialize each EnumerateChildren call site independently while
/// sharing the Visit implementation. When called through INodeOps&lt;ParentNode&gt;, both typeof
/// branches in Visit are live; through INodeOps&lt;ChildNode&gt;, only the ChildNode branch survives.
/// </summary>
internal struct MultiTypeOps(
    RefCountTable<ParentNode> parentTable,
    RefCountTable<ChildNode> childTable) : INodeOps<ParentNode>, INodeOps<ChildNode>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    readonly void INodeOps<ParentNode>.EnumerateChildren<TVisitor>(in ParentNode node, ref TVisitor visitor)
    {
        if (node.ParentRef.IsValid) visitor.Visit(node.ParentRef);
        if (node.ChildRef.IsValid) visitor.Visit(node.ChildRef);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    readonly void INodeOps<ChildNode>.EnumerateChildren<TVisitor>(in ChildNode node, ref TVisitor visitor)
    {
        if (node.LeftChild.IsValid) visitor.Visit(node.LeftChild);
        if (node.RightChild.IsValid) visitor.Visit(node.RightChild);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Visit<T>(Handle<T> handle) where T : struct
    {
        if (typeof(T) == typeof(ParentNode))
            parentTable.Decrement(Unsafe.As<Handle<T>, Handle<ParentNode>>(ref handle));
        else if (typeof(T) == typeof(ChildNode))
            childTable.Decrement(Unsafe.As<Handle<T>, Handle<ChildNode>>(ref handle));
    }
}
