using System.Runtime.InteropServices;
using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class SnapshotSliceTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TreeNode
    {
        public int Left { get; set; }
        public int Right { get; set; }
        public int Value { get; set; }
    }

    private struct TreeHandler(UnsafeSlabArena<TreeNode> arena) : RefCountTable.IRefCountHandler
    {
        public readonly void OnFreed(int index, RefCountTable table)
        {
            ref readonly var node = ref arena[index];
            if (node.Left >= 0) table.Decrement(node.Left, this);
            if (node.Right >= 0) table.Decrement(node.Right, this);
            arena.Free(index);
        }
    }

    private struct TreeChildEnumerator : DagValidator.IChildEnumerator<TreeNode>
    {
        public readonly void GetChildren(in TreeNode node, List<int> children)
        {
            if (node.Left >= 0) children.Add(node.Left);
            if (node.Right >= 0) children.Add(node.Right);
        }
    }

    private static NodeStore<TreeNode, TreeHandler> CreateStore()
    {
        var arena = new UnsafeSlabArena<TreeNode>();
        var refCounts = new RefCountTable();
        var handler = new TreeHandler(arena);
        var store = new NodeStore<TreeNode, TreeHandler>(arena, refCounts, handler);
        store.EnableValidation(new TreeChildEnumerator());
        return store;
    }

    private static int AllocNode(NodeStore<TreeNode, TreeHandler> store, int left, int right, int value)
    {
        var index = store.Arena.Allocate();
        store.RefCounts.EnsureCapacity(index + 1);
        store.Arena[index] = new TreeNode { Left = left, Right = right, Value = value };
        if (left >= 0) store.RefCounts.Increment(left);
        if (right >= 0) store.RefCounts.Increment(right);
        return index;
    }

    // ── Basic lifecycle ──────────────────────────────────────────────────

    [Fact]
    public void SingleRoot_IncrementThenDecrement_FreesEverything()
    {
        var store = CreateStore();

        var leaf = AllocNode(store, -1, -1, 1);
        var root = AllocNode(store, leaf, -1, 0);

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

        var a = AllocNode(store, -1, -1, 1);
        var b = AllocNode(store, -1, -1, 2);
        var c = AllocNode(store, -1, -1, 3);

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
        store.RefCounts.EnsureCapacity(3);
        var a = store.Arena.Allocate();
        var b = store.Arena.Allocate();

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
        store.RefCounts.EnsureCapacity(3);
        var a = store.Arena.Allocate();
        var b = store.Arena.Allocate();

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
        store.RefCounts.EnsureCapacity(3);
        var a = store.Arena.Allocate();
        var b = store.Arena.Allocate();

        var sharedList = new List<int>();

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
        var b = AllocNode(store, -1, -1, 2);
        var c = AllocNode(store, -1, -1, 3);
        var a = AllocNode(store, b, c, 1);
        var root1 = AllocNode(store, a, -1, 10);
        var root2 = AllocNode(store, a, -1, 20);

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

        var b = AllocNode(store, -1, -1, 2);
        var c = AllocNode(store, -1, -1, 3);
        var a = AllocNode(store, b, c, 1);
        var root1 = AllocNode(store, a, -1, 10);
        var root2 = AllocNode(store, a, -1, 20);

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
        var c = AllocNode(store, -1, -1, 10);
        var d = AllocNode(store, -1, -1, 11);
        var e = AllocNode(store, -1, -1, 12);
        var f = AllocNode(store, -1, -1, 13);
        var a = AllocNode(store, c, d, 1);
        var b = AllocNode(store, e, f, 2);
        var root = AllocNode(store, a, b, 0);

        var slice1 = new SnapshotSlice<TreeNode, TreeHandler>(store);
        slice1.AddRoot(root);
        slice1.IncrementRootRefCounts();

        // Path-copy: change leaf c → c'. New spine: a' → (c', d), root' → (a', b).
        var cPrime = AllocNode(store, -1, -1, 99);
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

        var shared = AllocNode(store, -1, -1, 99);
        var roots = new int[3];
        var slices = new SnapshotSlice<TreeNode, TreeHandler>[3];

        for (var i = 0; i < 3; i++)
        {
            roots[i] = AllocNode(store, shared, -1, i);
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
