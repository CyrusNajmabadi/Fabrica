using System.Runtime.InteropServices;
using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class SnapshotSliceTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TreeNode
    {
        public Handle<TreeNode> Left { get; set; }
        public Handle<TreeNode> Right { get; set; }
        public int Value { get; set; }
    }

    private struct TreeHandler(UnsafeSlabArena<TreeNode> arena, TreeChildEnumerator enumerator) : RefCountTable<TreeNode>.IRefCountHandler
    {
        public readonly void OnFreed(Handle<TreeNode> handle, RefCountTable<TreeNode> table)
        {
            ref readonly var node = ref arena[handle];
            var visitor = new RefCountTable<TreeNode>.DecrementNodeRefCountVisitor<TreeHandler>(table, this);
            enumerator.EnumerateChildren(in node, ref visitor);
            arena.Free(handle);
        }
    }

    private struct TreeChildEnumerator : INodeChildEnumerator<TreeNode>
    {
        public readonly void EnumerateChildren<TAction>(in TreeNode node, ref TAction visitor)
            where TAction : struct, INodeVisitor
        {
            if (node.Left.IsValid) visitor.Visit(node.Left);
            if (node.Right.IsValid) visitor.Visit(node.Right);
        }

        public readonly void EnumerateChildren<TAction, TContext>(in TreeNode node, in TContext context, ref TAction visitor)
            where TAction : struct, INodeVisitor<TContext>
        {
            if (node.Left.IsValid) visitor.Visit(node.Left, in context);
            if (node.Right.IsValid) visitor.Visit(node.Right, in context);
        }

        public readonly void RewriteChildren<TRewriter>(ref TreeNode node, ref TRewriter rewriter)
            where TRewriter : struct, INodeHandleRewriter
        {
            if (node.Left.Index != -1) { var h = node.Left; rewriter.Rewrite(ref h); node.Left = h; }
            if (node.Right.Index != -1) { var h = node.Right; rewriter.Rewrite(ref h); node.Right = h; }
        }
    }

    private static NodeStore<TreeNode, TreeHandler> CreateStore()
    {
        var arena = new UnsafeSlabArena<TreeNode>();
        var refCounts = new RefCountTable<TreeNode>();
        var enumerator = new TreeChildEnumerator();
        var handler = new TreeHandler(arena, enumerator);
        var store = new NodeStore<TreeNode, TreeHandler>(arena, refCounts, handler);
        store.EnableValidation(enumerator);
        return store;
    }

    private static Handle<TreeNode> AllocNode(
        NodeStore<TreeNode, TreeHandler> store,
        Handle<TreeNode> left,
        Handle<TreeNode> right,
        int value)
    {
        var handle = store.Arena.Allocate();
        store.RefCounts.EnsureCapacity(handle.Index + 1);
        store.Arena[handle] = new TreeNode { Left = left, Right = right, Value = value };
        if (left.IsValid) store.RefCounts.Increment(left);
        if (right.IsValid) store.RefCounts.Increment(right);
        return handle;
    }

    // ── Basic lifecycle ──────────────────────────────────────────────────

    [Fact]
    public void SingleRoot_IncrementThenDecrement_FreesEverything()
    {
        var store = CreateStore();

        var leaf = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 1);
        var root = AllocNode(store, leaf, Handle<TreeNode>.None, 0);

        var slice = new SnapshotSlice<TreeNode, TreeHandler>(store);
        slice.AddRoot(root);
        Assert.Equal(1, slice.Count);

        slice.IncrementRootRefCounts();
        Assert.Equal(1, store.RefCounts.GetCount(root));
        Assert.Equal(1, store.RefCounts.GetCount(leaf));

        slice.DecrementRootRefCounts();
        Assert.Equal(0, store.RefCounts.GetCount(root));
        Assert.Equal(0, store.RefCounts.GetCount(leaf));
    }

    [Fact]
    public void MultipleRoots_AllIncrementedAndDecremented()
    {
        var store = CreateStore();

        var a = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 1);
        var b = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 2);
        var c = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 3);

        var slice = new SnapshotSlice<TreeNode, TreeHandler>(store);
        slice.AddRoot(a);
        slice.AddRoot(b);
        slice.AddRoot(c);
        Assert.Equal(3, slice.Count);

        slice.IncrementRootRefCounts();
        Assert.Equal(1, store.RefCounts.GetCount(a));
        Assert.Equal(1, store.RefCounts.GetCount(b));
        Assert.Equal(1, store.RefCounts.GetCount(c));

        slice.DecrementRootRefCounts();
        Assert.Equal(0, store.RefCounts.GetCount(a));
        Assert.Equal(0, store.RefCounts.GetCount(b));
        Assert.Equal(0, store.RefCounts.GetCount(c));
    }

    [Fact]
    public void Roots_ReturnsCorrectSpan()
    {
        var store = CreateStore();
        var a = store.Arena.Allocate();
        var b = store.Arena.Allocate();
        store.RefCounts.EnsureCapacity(Math.Max(a.Index, b.Index) + 1);

        var slice = new SnapshotSlice<TreeNode, TreeHandler>(store);
        slice.AddRoot(a);
        slice.AddRoot(b);

        var roots = slice.Roots;
        Assert.Equal(2, roots.Length);
        Assert.Equal(a, roots[0]);
        Assert.Equal(b, roots[1]);
    }

    [Fact]
    public void Clear_ResetsForReuse()
    {
        var store = CreateStore();
        var a = store.Arena.Allocate();
        var b = store.Arena.Allocate();
        store.RefCounts.EnsureCapacity(Math.Max(a.Index, b.Index) + 1);

        var slice = new SnapshotSlice<TreeNode, TreeHandler>(store);
        slice.AddRoot(a);
        slice.AddRoot(b);
        Assert.Equal(2, slice.Count);

        slice.Clear();
        Assert.Equal(0, slice.Count);
        Assert.True(slice.Roots.IsEmpty);

        slice.AddRoot(a);
        Assert.Equal(1, slice.Count);
    }

    [Fact]
    public void PooledList_CanBeReusedAcrossSlices()
    {
        var store = CreateStore();
        var a = store.Arena.Allocate();
        var b = store.Arena.Allocate();
        store.RefCounts.EnsureCapacity(Math.Max(a.Index, b.Index) + 1);

        var sharedList = new List<Handle<TreeNode>>();

        var slice1 = new SnapshotSlice<TreeNode, TreeHandler>(store, sharedList);
        slice1.AddRoot(a);
        Assert.Equal(1, slice1.Count);
        slice1.Clear();

        var slice2 = new SnapshotSlice<TreeNode, TreeHandler>(store, sharedList);
        slice2.AddRoot(b);
        Assert.Equal(1, slice2.Count);
        Assert.Equal(b, slice2.Roots[0]);
    }

    // ── Multi-snapshot structural sharing ────────────────────────────────

    [Fact]
    public void TwoSnapshots_SharedSubtree_ReleaseFirstThenSecond()
    {
        var store = CreateStore();

        //  Shared subtree:   a → (b, c)
        //  Snapshot 1 root: root1 → a
        //  Snapshot 2 root: root2 → a  (structural sharing)
        var b = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 2);
        var c = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 3);
        var a = AllocNode(store, b, c, 1);
        var root1 = AllocNode(store, a, Handle<TreeNode>.None, 10);
        var root2 = AllocNode(store, a, Handle<TreeNode>.None, 20);

        var slice1 = new SnapshotSlice<TreeNode, TreeHandler>(store);
        slice1.AddRoot(root1);
        slice1.IncrementRootRefCounts();

        var slice2 = new SnapshotSlice<TreeNode, TreeHandler>(store);
        slice2.AddRoot(root2);
        slice2.IncrementRootRefCounts();

        Assert.Equal(2, store.RefCounts.GetCount(a));

        slice1.DecrementRootRefCounts();
        Assert.Equal(0, store.RefCounts.GetCount(root1));
        Assert.Equal(1, store.RefCounts.GetCount(a));
        Assert.Equal(1, store.RefCounts.GetCount(b));
        Assert.Equal(1, store.RefCounts.GetCount(c));

        slice2.DecrementRootRefCounts();
        Assert.Equal(0, store.RefCounts.GetCount(a));
        Assert.Equal(0, store.RefCounts.GetCount(b));
        Assert.Equal(0, store.RefCounts.GetCount(c));
    }

    [Fact]
    public void TwoSnapshots_SharedSubtree_ReleaseSecondThenFirst()
    {
        var store = CreateStore();

        var b = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 2);
        var c = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 3);
        var a = AllocNode(store, b, c, 1);
        var root1 = AllocNode(store, a, Handle<TreeNode>.None, 10);
        var root2 = AllocNode(store, a, Handle<TreeNode>.None, 20);

        var slice1 = new SnapshotSlice<TreeNode, TreeHandler>(store);
        slice1.AddRoot(root1);
        slice1.IncrementRootRefCounts();

        var slice2 = new SnapshotSlice<TreeNode, TreeHandler>(store);
        slice2.AddRoot(root2);
        slice2.IncrementRootRefCounts();

        slice2.DecrementRootRefCounts();
        Assert.Equal(0, store.RefCounts.GetCount(root2));
        Assert.Equal(1, store.RefCounts.GetCount(a));

        slice1.DecrementRootRefCounts();
        Assert.Equal(0, store.RefCounts.GetCount(a));
        Assert.Equal(0, store.RefCounts.GetCount(b));
        Assert.Equal(0, store.RefCounts.GetCount(c));
    }

    // ── Path-copy simulation ─────────────────────────────────────────────

    [Fact]
    public void PathCopy_OldSpineFreed_SharedSubtreesSurvive()
    {
        var store = CreateStore();

        // Original tree:
        //       root
        //      /    \
        //     a      b
        //    / \    / \
        //   c   d  e   f
        var c = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 10);
        var d = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 11);
        var e = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 12);
        var f = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 13);
        var a = AllocNode(store, c, d, 1);
        var b = AllocNode(store, e, f, 2);
        var root = AllocNode(store, a, b, 0);

        var slice1 = new SnapshotSlice<TreeNode, TreeHandler>(store);
        slice1.AddRoot(root);
        slice1.IncrementRootRefCounts();

        // Path-copy: change leaf c → c'. New spine: a' → (c', d), root' → (a', b).
        var cPrime = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 99);
        var aPrime = AllocNode(store, cPrime, d, 1);
        var rootPrime = AllocNode(store, aPrime, b, 0);

        var slice2 = new SnapshotSlice<TreeNode, TreeHandler>(store);
        slice2.AddRoot(rootPrime);
        slice2.IncrementRootRefCounts();

        // d has 2 parents (a, a'), b has 2 parents (root, root')
        Assert.Equal(2, store.RefCounts.GetCount(d));
        Assert.Equal(2, store.RefCounts.GetCount(b));

        // Release old snapshot
        slice1.DecrementRootRefCounts();

        // Old spine freed: root, a, c
        Assert.Equal(0, store.RefCounts.GetCount(root));
        Assert.Equal(0, store.RefCounts.GetCount(a));
        Assert.Equal(0, store.RefCounts.GetCount(c));

        // Shared subtrees survive: d, b, e, f
        Assert.Equal(1, store.RefCounts.GetCount(d));
        Assert.Equal(1, store.RefCounts.GetCount(b));
        Assert.Equal(1, store.RefCounts.GetCount(e));
        Assert.Equal(1, store.RefCounts.GetCount(f));

        // New spine alive
        Assert.Equal(1, store.RefCounts.GetCount(rootPrime));
        Assert.Equal(1, store.RefCounts.GetCount(aPrime));
        Assert.Equal(1, store.RefCounts.GetCount(cPrime));

        // Release new snapshot — everything freed
        slice2.DecrementRootRefCounts();

        Assert.Equal(0, store.RefCounts.GetCount(rootPrime));
        Assert.Equal(0, store.RefCounts.GetCount(aPrime));
        Assert.Equal(0, store.RefCounts.GetCount(cPrime));
        Assert.Equal(0, store.RefCounts.GetCount(d));
        Assert.Equal(0, store.RefCounts.GetCount(b));
        Assert.Equal(0, store.RefCounts.GetCount(e));
        Assert.Equal(0, store.RefCounts.GetCount(f));
    }

    // ── Release order permutations ───────────────────────────────────────

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(0, 2, 1)]
    [InlineData(1, 0, 2)]
    [InlineData(1, 2, 0)]
    [InlineData(2, 0, 1)]
    [InlineData(2, 1, 0)]
    public void ThreeSnapshots_SharedNode_AllReleaseOrdersCorrect(int first, int second, int third)
    {
        var store = CreateStore();

        var shared = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 99);
        var roots = new Handle<TreeNode>[3];
        var slices = new SnapshotSlice<TreeNode, TreeHandler>[3];

        for (var i = 0; i < 3; i++)
        {
            roots[i] = AllocNode(store, shared, Handle<TreeNode>.None, i);
            slices[i] = new SnapshotSlice<TreeNode, TreeHandler>(store);
            slices[i].AddRoot(roots[i]);
            slices[i].IncrementRootRefCounts();
        }

        Assert.Equal(3, store.RefCounts.GetCount(shared));

        var order = new[] { first, second, third };
        for (var step = 0; step < 3; step++)
        {
            var idx = order[step];
            slices[idx].DecrementRootRefCounts();
            Assert.Equal(0, store.RefCounts.GetCount(roots[idx]));

            var remainingCount = 2 - step;
            Assert.Equal(remainingCount, store.RefCounts.GetCount(shared));
        }
    }
}
