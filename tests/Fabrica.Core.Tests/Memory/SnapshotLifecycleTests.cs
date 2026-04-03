using System.Runtime.InteropServices;
using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

/// <summary>
/// Simulates the full snapshot lifecycle through a producer-consumer queue: sequential creation with
/// structural sharing (path-copy), publish (increment roots), and release (decrement roots) in various
/// orders. Verifies refcount correctness at each step.
/// </summary>
public class SnapshotLifecycleTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TreeNode
    {
        public int Left { get; set; }
        public int Right { get; set; }
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

    private readonly NodeStore<TreeNode, TreeHandler> _store;

    public SnapshotLifecycleTests()
    {
        var arena = new UnsafeSlabArena<TreeNode>();
        var refCounts = new RefCountTable();
        var handler = new TreeHandler(arena);
        _store = new NodeStore<TreeNode, TreeHandler>(arena, refCounts, handler);
        _store.EnableValidation(new TreeChildEnumerator());
    }

    private int AllocNode(int left, int right)
    {
        var index = _store.Arena.Allocate();
        _store.RefCounts.EnsureCapacity(index + 1);
        _store.Arena[index] = new TreeNode { Left = left, Right = right };
        if (left >= 0) _store.RefCounts.Increment(left);
        if (right >= 0) _store.RefCounts.Increment(right);
        return index;
    }

    private int BuildPerfectTree(int depth)
    {
        if (depth == 0)
            return this.AllocNode(-1, -1);

        var left = this.BuildPerfectTree(depth - 1);
        var right = this.BuildPerfectTree(depth - 1);
        return this.AllocNode(left, right);
    }

    private int PathCopyLeftSpine(int oldRoot, int depth)
    {
        if (depth == 0)
            return this.AllocNode(-1, -1);

        ref readonly var old = ref _store.Arena[oldRoot];
        var newLeft = this.PathCopyLeftSpine(old.Left, depth - 1);
        return this.AllocNode(newLeft, old.Right);
    }

    private void AssertNodeAlive(int index)
        => Assert.True(_store.RefCounts.GetCount(index) > 0, $"Node {index} should be alive (refcount > 0).");

    private void AssertNodeDead(int index)
        => Assert.Equal(0, _store.RefCounts.GetCount(index));

    // ── FIFO release (consumer catches up in order) ──────────────────────

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(10)]
    public void FIFO_Release_SequentialSnapshots(int snapshotCount)
    {
        var root = this.BuildPerfectTree(3);
        var slices = new SnapshotSlice<TreeNode, TreeHandler>[snapshotCount];

        slices[0] = new SnapshotSlice<TreeNode, TreeHandler>(_store);
        slices[0].AddRoot(root);
        slices[0].IncrementRootRefCounts();

        for (var i = 1; i < snapshotCount; i++)
        {
            var newRoot = this.PathCopyLeftSpine(root, 3);
            slices[i] = new SnapshotSlice<TreeNode, TreeHandler>(_store);
            slices[i].AddRoot(newRoot);
            slices[i].IncrementRootRefCounts();
            root = newRoot;
        }

        // Release FIFO (oldest first)
        for (var i = 0; i < snapshotCount; i++)
        {
            slices[i].DecrementRootRefCounts();

            // The latest snapshot's root should still be alive
            if (i < snapshotCount - 1)
                this.AssertNodeAlive(slices[snapshotCount - 1].Roots[0]);
        }

        // After releasing all, the last root should be dead
        this.AssertNodeDead(slices[snapshotCount - 1].Roots[0]);
    }

    // ── LIFO release ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void LIFO_Release_SequentialSnapshots(int snapshotCount)
    {
        var root = this.BuildPerfectTree(3);
        var slices = new SnapshotSlice<TreeNode, TreeHandler>[snapshotCount];

        slices[0] = new SnapshotSlice<TreeNode, TreeHandler>(_store);
        slices[0].AddRoot(root);
        slices[0].IncrementRootRefCounts();

        for (var i = 1; i < snapshotCount; i++)
        {
            var newRoot = this.PathCopyLeftSpine(root, 3);
            slices[i] = new SnapshotSlice<TreeNode, TreeHandler>(_store);
            slices[i].AddRoot(newRoot);
            slices[i].IncrementRootRefCounts();
            root = newRoot;
        }

        // Release LIFO (newest first)
        for (var i = snapshotCount - 1; i >= 0; i--)
        {
            slices[i].DecrementRootRefCounts();

            if (i > 0)
                this.AssertNodeAlive(slices[0].Roots[0]);
        }

        this.AssertNodeDead(slices[0].Roots[0]);
    }

    // ── Interleaved create and release ───────────────────────────────────

    [Fact]
    public void Interleaved_CreateAndRelease_SteadyState()
    {
        var root = this.BuildPerfectTree(3);

        var prevSlice = new SnapshotSlice<TreeNode, TreeHandler>(_store);
        prevSlice.AddRoot(root);
        prevSlice.IncrementRootRefCounts();

        var initialAllocCount = _store.Arena.GetTestAccessor().Count;

        for (var i = 0; i < 50; i++)
        {
            var newRoot = this.PathCopyLeftSpine(root, 3);
            var newSlice = new SnapshotSlice<TreeNode, TreeHandler>(_store);
            newSlice.AddRoot(newRoot);
            newSlice.IncrementRootRefCounts();

            prevSlice.DecrementRootRefCounts();

            this.AssertNodeAlive(newRoot);
            root = newRoot;
            prevSlice = newSlice;
        }

        // After 50 iterations, active node count should be roughly the same as the initial tree
        // (old spines are freed as fast as new ones are created).
        var finalAllocCount = _store.Arena.GetTestAccessor().Count;
        Assert.True(finalAllocCount <= initialAllocCount + 10,
            $"Expected steady-state allocation count near {initialAllocCount}, got {finalAllocCount}.");

        prevSlice.DecrementRootRefCounts();
        this.AssertNodeDead(root);
    }

    // ── Multiple roots per snapshot ──────────────────────────────────────

    [Fact]
    public void MultipleRoots_IndependentTrees_AllFreedOnRelease()
    {
        var root1 = this.BuildPerfectTree(2);
        var root2 = this.BuildPerfectTree(2);
        var root3 = this.BuildPerfectTree(2);

        var slice = new SnapshotSlice<TreeNode, TreeHandler>(_store);
        slice.AddRoot(root1);
        slice.AddRoot(root2);
        slice.AddRoot(root3);
        slice.IncrementRootRefCounts();

        Assert.Equal(3, slice.Count);
        this.AssertNodeAlive(root1);
        this.AssertNodeAlive(root2);
        this.AssertNodeAlive(root3);

        slice.DecrementRootRefCounts();

        this.AssertNodeDead(root1);
        this.AssertNodeDead(root2);
        this.AssertNodeDead(root3);
    }

    [Fact]
    public void MultipleRoots_SharedSubtree_SurvivesPartialRootRelease()
    {
        // Shared leaf
        var shared = this.AllocNode(-1, -1);
        var root1 = this.AllocNode(shared, -1);
        var root2 = this.AllocNode(-1, shared);

        var slice = new SnapshotSlice<TreeNode, TreeHandler>(_store);
        slice.AddRoot(root1);
        slice.AddRoot(root2);
        slice.IncrementRootRefCounts();

        Assert.Equal(2, _store.RefCounts.GetCount(shared));

        slice.DecrementRootRefCounts();

        this.AssertNodeDead(root1);
        this.AssertNodeDead(root2);
        this.AssertNodeDead(shared);
    }

    // ── Windowed release (producer ahead by N) ───────────────────────────

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(10)]
    public void WindowedRelease_ProducerAhead_ConsumerCatchesUp(int windowSize)
    {
        var root = this.BuildPerfectTree(3);
        var queue = new Queue<SnapshotSlice<TreeNode, TreeHandler>>();

        var initialSlice = new SnapshotSlice<TreeNode, TreeHandler>(_store);
        initialSlice.AddRoot(root);
        initialSlice.IncrementRootRefCounts();
        queue.Enqueue(initialSlice);

        for (var i = 0; i < 30; i++)
        {
            var newRoot = this.PathCopyLeftSpine(root, 3);
            var newSlice = new SnapshotSlice<TreeNode, TreeHandler>(_store);
            newSlice.AddRoot(newRoot);
            newSlice.IncrementRootRefCounts();
            queue.Enqueue(newSlice);
            root = newRoot;

            while (queue.Count > windowSize)
            {
                var oldSlice = queue.Dequeue();
                oldSlice.DecrementRootRefCounts();
            }
        }

        // Drain remaining
        while (queue.Count > 0)
        {
            var oldSlice = queue.Dequeue();
            oldSlice.DecrementRootRefCounts();
        }

        this.AssertNodeDead(root);
    }

    // ── Permutation testing for small snapshot sets ───────────────────────

    [Fact]
    public void FourSnapshots_AllReleasePermutations()
    {
        var permutations = new List<int[]>();
        Permute([0, 1, 2, 3], 0, permutations);

        foreach (var perm in permutations)
        {
            // Fresh store per permutation so freed nodes from prior iterations don't interfere.
            var arena = new UnsafeSlabArena<TreeNode>();
            var refCounts = new RefCountTable();
            var handler = new TreeHandler(arena);
            var store = new NodeStore<TreeNode, TreeHandler>(arena, refCounts, handler);

            var root = BuildPerfectTreeInStore(store, 3);
            var slices = new SnapshotSlice<TreeNode, TreeHandler>[4];
            var currentRoot = root;

            for (var i = 0; i < 4; i++)
            {
                if (i > 0)
                    currentRoot = PathCopyLeftSpineInStore(store, currentRoot, 3);

                slices[i] = new SnapshotSlice<TreeNode, TreeHandler>(store);
                slices[i].AddRoot(currentRoot);
                slices[i].IncrementRootRefCounts();
            }

            foreach (var idx in perm)
                slices[idx].DecrementRootRefCounts();

            Assert.Equal(0, store.RefCounts.GetCount(slices[3].Roots[0]));
        }
    }

    private static int AllocNodeInStore(NodeStore<TreeNode, TreeHandler> store, int left, int right)
    {
        var index = store.Arena.Allocate();
        store.RefCounts.EnsureCapacity(index + 1);
        store.Arena[index] = new TreeNode { Left = left, Right = right };
        if (left >= 0) store.RefCounts.Increment(left);
        if (right >= 0) store.RefCounts.Increment(right);
        return index;
    }

    private static int BuildPerfectTreeInStore(NodeStore<TreeNode, TreeHandler> store, int depth)
    {
        if (depth == 0)
            return AllocNodeInStore(store, -1, -1);

        var left = BuildPerfectTreeInStore(store, depth - 1);
        var right = BuildPerfectTreeInStore(store, depth - 1);
        return AllocNodeInStore(store, left, right);
    }

    private static int PathCopyLeftSpineInStore(NodeStore<TreeNode, TreeHandler> store, int oldRoot, int depth)
    {
        if (depth == 0)
            return AllocNodeInStore(store, -1, -1);

        ref readonly var old = ref store.Arena[oldRoot];
        var newLeft = PathCopyLeftSpineInStore(store, old.Left, depth - 1);
        return AllocNodeInStore(store, newLeft, old.Right);
    }

    private static void Permute(int[] arr, int start, List<int[]> results)
    {
        if (start >= arr.Length)
        {
            results.Add((int[])arr.Clone());
            return;
        }

        for (var i = start; i < arr.Length; i++)
        {
            (arr[start], arr[i]) = (arr[i], arr[start]);
            Permute(arr, start + 1, results);
            (arr[start], arr[i]) = (arr[i], arr[start]);
        }
    }
}
