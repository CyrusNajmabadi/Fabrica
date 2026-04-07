using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class SnapshotSliceTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TreeNode
    {
        public Handle<TreeNode> Left;
        public Handle<TreeNode> Right;
        public int Value;
    }

    private struct TreeNodeOps : INodeOps<TreeNode>
    {
        internal GlobalNodeStore<TreeNode, TreeNodeOps> Store;

        public readonly void EnumerateChildren<TVisitor>(in TreeNode node, ref TVisitor visitor)
            where TVisitor : struct, INodeVisitor
        {
            if (node.Left.IsValid) visitor.Visit(node.Left);
            if (node.Right.IsValid) visitor.Visit(node.Right);
        }

        public readonly void Visit<T>(Handle<T> handle)
            where T : struct
        {
            if (typeof(T) == typeof(TreeNode))
            {
                Store.DecrementRefCount(Unsafe.As<Handle<T>, Handle<TreeNode>>(ref handle));
            }
        }
    }

    private static GlobalNodeStore<TreeNode, TreeNodeOps> CreateStore()
    {
        var arena = new UnsafeSlabArena<TreeNode>();
        var refCounts = new RefCountTable<TreeNode>();
        var store = GlobalNodeStore<TreeNode, TreeNodeOps>.TestAccessor.Create(arena, refCounts);
        store.SetNodeOps(new TreeNodeOps { Store = store });
        store.EnableValidation();
        return store;
    }

    private static Handle<TreeNode> AllocNode(
        GlobalNodeStore<TreeNode, TreeNodeOps> store,
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

        var slice = new SnapshotSlice<TreeNode, TreeNodeOps>(store, new UnsafeList<Handle<TreeNode>>());
        slice.AddRoot(root);
        Assert.Equal(1, slice.Count);

        slice.IncrementRootRefCounts();
        Assert.Equal(1, store.RefCounts.GetCount(root));
        Assert.Equal(1, store.RefCounts.GetCount(leaf));

        store.DecrementRoots(slice.Roots);
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

        var slice = new SnapshotSlice<TreeNode, TreeNodeOps>(store, new UnsafeList<Handle<TreeNode>>());
        slice.AddRoot(a);
        slice.AddRoot(b);
        slice.AddRoot(c);
        Assert.Equal(3, slice.Count);

        slice.IncrementRootRefCounts();
        Assert.Equal(1, store.RefCounts.GetCount(a));
        Assert.Equal(1, store.RefCounts.GetCount(b));
        Assert.Equal(1, store.RefCounts.GetCount(c));

        store.DecrementRoots(slice.Roots);
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

        var slice = new SnapshotSlice<TreeNode, TreeNodeOps>(store, new UnsafeList<Handle<TreeNode>>());
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

        var slice = new SnapshotSlice<TreeNode, TreeNodeOps>(store, new UnsafeList<Handle<TreeNode>>());
        slice.AddRoot(a);
        slice.AddRoot(b);
        Assert.Equal(2, slice.Count);

        slice.RootHandles.Reset();
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

        var sharedList = new UnsafeList<Handle<TreeNode>>();

        var slice1 = new SnapshotSlice<TreeNode, TreeNodeOps>(store, sharedList);
        slice1.AddRoot(a);
        Assert.Equal(1, slice1.Count);
        slice1.RootHandles.Reset();

        var slice2 = new SnapshotSlice<TreeNode, TreeNodeOps>(store, sharedList);
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

        var slice1 = new SnapshotSlice<TreeNode, TreeNodeOps>(store, new UnsafeList<Handle<TreeNode>>());
        slice1.AddRoot(root1);
        slice1.IncrementRootRefCounts();

        var slice2 = new SnapshotSlice<TreeNode, TreeNodeOps>(store, new UnsafeList<Handle<TreeNode>>());
        slice2.AddRoot(root2);
        slice2.IncrementRootRefCounts();

        Assert.Equal(2, store.RefCounts.GetCount(a));

        store.DecrementRoots(slice1.Roots);
        Assert.Equal(0, store.RefCounts.GetCount(root1));
        Assert.Equal(1, store.RefCounts.GetCount(a));
        Assert.Equal(1, store.RefCounts.GetCount(b));
        Assert.Equal(1, store.RefCounts.GetCount(c));

        store.DecrementRoots(slice2.Roots);
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

        var slice1 = new SnapshotSlice<TreeNode, TreeNodeOps>(store, new UnsafeList<Handle<TreeNode>>());
        slice1.AddRoot(root1);
        slice1.IncrementRootRefCounts();

        var slice2 = new SnapshotSlice<TreeNode, TreeNodeOps>(store, new UnsafeList<Handle<TreeNode>>());
        slice2.AddRoot(root2);
        slice2.IncrementRootRefCounts();

        store.DecrementRoots(slice2.Roots);
        Assert.Equal(0, store.RefCounts.GetCount(root2));
        Assert.Equal(1, store.RefCounts.GetCount(a));

        store.DecrementRoots(slice1.Roots);
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

        var slice1 = new SnapshotSlice<TreeNode, TreeNodeOps>(store, new UnsafeList<Handle<TreeNode>>());
        slice1.AddRoot(root);
        slice1.IncrementRootRefCounts();

        // Path-copy: change leaf c → c'. New spine: a' → (c', d), root' → (a', b).
        var cPrime = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 99);
        var aPrime = AllocNode(store, cPrime, d, 1);
        var rootPrime = AllocNode(store, aPrime, b, 0);

        var slice2 = new SnapshotSlice<TreeNode, TreeNodeOps>(store, new UnsafeList<Handle<TreeNode>>());
        slice2.AddRoot(rootPrime);
        slice2.IncrementRootRefCounts();

        // d has 2 parents (a, a'), b has 2 parents (root, root')
        Assert.Equal(2, store.RefCounts.GetCount(d));
        Assert.Equal(2, store.RefCounts.GetCount(b));

        // Release old snapshot
        store.DecrementRoots(slice1.Roots);

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
        store.DecrementRoots(slice2.Roots);

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
        var slices = new SnapshotSlice<TreeNode, TreeNodeOps>[3];

        for (var i = 0; i < 3; i++)
        {
            roots[i] = AllocNode(store, shared, Handle<TreeNode>.None, i);
            slices[i] = new SnapshotSlice<TreeNode, TreeNodeOps>(store, new UnsafeList<Handle<TreeNode>>());
            slices[i].AddRoot(roots[i]);
            slices[i].IncrementRootRefCounts();
        }

        Assert.Equal(3, store.RefCounts.GetCount(shared));

        var order = new[] { first, second, third };
        for (var step = 0; step < 3; step++)
        {
            var snapshotIndex = order[step];
            store.DecrementRoots(slices[snapshotIndex].Roots);
            Assert.Equal(0, store.RefCounts.GetCount(roots[snapshotIndex]));

            var remainingCount = 2 - step;
            Assert.Equal(remainingCount, store.RefCounts.GetCount(shared));
        }
    }
}
