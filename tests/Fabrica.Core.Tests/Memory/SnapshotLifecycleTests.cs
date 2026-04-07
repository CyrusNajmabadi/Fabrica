using System.Runtime.CompilerServices;
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
        public Handle<TreeNode> Left;
        public Handle<TreeNode> Right;
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

        public readonly void Visit<T>(Handle<T> handle) where T : struct
        {
            if (typeof(T) == typeof(TreeNode))
            {
                var c = Unsafe.As<Handle<T>, Handle<TreeNode>>(ref handle);
                Store.DecrementRefCount(c);
            }
        }
    }

    private readonly GlobalNodeStore<TreeNode, TreeNodeOps> _store;

    public SnapshotLifecycleTests()
    {
        var arena = new UnsafeSlabArena<TreeNode>();
        var refCounts = new RefCountTable<TreeNode>();
        _store = GlobalNodeStore<TreeNode, TreeNodeOps>.TestAccessor.Create(arena, refCounts);
        _store.SetNodeOps(new TreeNodeOps { Store = _store });
        _store.EnableValidation();
    }

    private Handle<TreeNode> AllocNode(Handle<TreeNode> left, Handle<TreeNode> right)
    {
        var handle = _store.Arena.Allocate();
        _store.RefCounts.EnsureCapacity(handle.Index + 1);
        _store.Arena[handle] = new TreeNode { Left = left, Right = right };
        if (left.IsValid) _store.RefCounts.Increment(left);
        if (right.IsValid) _store.RefCounts.Increment(right);
        return handle;
    }

    private Handle<TreeNode> BuildPerfectTree(int depth)
    {
        if (depth == 0)
            return this.AllocNode(Handle<TreeNode>.None, Handle<TreeNode>.None);

        var left = this.BuildPerfectTree(depth - 1);
        var right = this.BuildPerfectTree(depth - 1);
        return this.AllocNode(left, right);
    }

    private Handle<TreeNode> PathCopyLeftSpine(Handle<TreeNode> oldRoot, int depth)
    {
        if (depth == 0)
            return this.AllocNode(Handle<TreeNode>.None, Handle<TreeNode>.None);

        ref readonly var old = ref _store.Arena[oldRoot];
        var newLeft = this.PathCopyLeftSpine(old.Left, depth - 1);
        return this.AllocNode(newLeft, old.Right);
    }

    private void AssertNodeAlive(Handle<TreeNode> handle)
        => Assert.True(_store.RefCounts.GetCount(handle) > 0, $"Node {handle} should be alive (refcount > 0).");

    private void AssertNodeDead(Handle<TreeNode> handle)
        => Assert.Equal(0, _store.RefCounts.GetCount(handle));

    // ── FIFO release (consumer catches up in order) ──────────────────────

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(10)]
    public void FIFO_Release_SequentialSnapshots(int snapshotCount)
    {
        var root = this.BuildPerfectTree(3);
        var slices = new SnapshotSlice<TreeNode, TreeNodeOps>[snapshotCount];

        slices[0] = new SnapshotSlice<TreeNode, TreeNodeOps>(_store, new UnsafeList<Handle<TreeNode>>());
        slices[0].AddRoot(root);
        slices[0].IncrementRootRefCounts();

        for (var i = 1; i < snapshotCount; i++)
        {
            var newRoot = this.PathCopyLeftSpine(root, 3);
            slices[i] = new SnapshotSlice<TreeNode, TreeNodeOps>(_store, new UnsafeList<Handle<TreeNode>>());
            slices[i].AddRoot(newRoot);
            slices[i].IncrementRootRefCounts();
            root = newRoot;
        }

        // Release FIFO (oldest first)
        for (var i = 0; i < snapshotCount; i++)
        {
            slices[i].Release();

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
        var slices = new SnapshotSlice<TreeNode, TreeNodeOps>[snapshotCount];

        slices[0] = new SnapshotSlice<TreeNode, TreeNodeOps>(_store, new UnsafeList<Handle<TreeNode>>());
        slices[0].AddRoot(root);
        slices[0].IncrementRootRefCounts();

        for (var i = 1; i < snapshotCount; i++)
        {
            var newRoot = this.PathCopyLeftSpine(root, 3);
            slices[i] = new SnapshotSlice<TreeNode, TreeNodeOps>(_store, new UnsafeList<Handle<TreeNode>>());
            slices[i].AddRoot(newRoot);
            slices[i].IncrementRootRefCounts();
            root = newRoot;
        }

        // Release LIFO (newest first)
        for (var i = snapshotCount - 1; i >= 0; i--)
        {
            slices[i].Release();

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

        var prevSlice = new SnapshotSlice<TreeNode, TreeNodeOps>(_store, new UnsafeList<Handle<TreeNode>>());
        prevSlice.AddRoot(root);
        prevSlice.IncrementRootRefCounts();

        var initialAllocCount = _store.Arena.GetTestAccessor().Count;

        for (var i = 0; i < 50; i++)
        {
            var newRoot = this.PathCopyLeftSpine(root, 3);
            var newSlice = new SnapshotSlice<TreeNode, TreeNodeOps>(_store, new UnsafeList<Handle<TreeNode>>());
            newSlice.AddRoot(newRoot);
            newSlice.IncrementRootRefCounts();

            prevSlice.Release();

            this.AssertNodeAlive(newRoot);
            root = newRoot;
            prevSlice = newSlice;
        }

        // After 50 iterations, active node count should be roughly the same as the initial tree
        // (old spines are freed as fast as new ones are created).
        var finalAllocCount = _store.Arena.GetTestAccessor().Count;
        Assert.True(finalAllocCount <= initialAllocCount + 10,
            $"Expected steady-state allocation count near {initialAllocCount}, got {finalAllocCount}.");

        prevSlice.Release();
        this.AssertNodeDead(root);
    }

    // ── Multiple roots per snapshot ──────────────────────────────────────

    [Fact]
    public void MultipleRoots_IndependentTrees_AllFreedOnRelease()
    {
        var root1 = this.BuildPerfectTree(2);
        var root2 = this.BuildPerfectTree(2);
        var root3 = this.BuildPerfectTree(2);

        var slice = new SnapshotSlice<TreeNode, TreeNodeOps>(_store, new UnsafeList<Handle<TreeNode>>());
        slice.AddRoot(root1);
        slice.AddRoot(root2);
        slice.AddRoot(root3);
        slice.IncrementRootRefCounts();

        Assert.Equal(3, slice.Count);
        this.AssertNodeAlive(root1);
        this.AssertNodeAlive(root2);
        this.AssertNodeAlive(root3);

        slice.Release();

        this.AssertNodeDead(root1);
        this.AssertNodeDead(root2);
        this.AssertNodeDead(root3);
    }

    [Fact]
    public void MultipleRoots_SharedSubtree_SurvivesPartialRootRelease()
    {
        // Shared leaf
        var shared = this.AllocNode(Handle<TreeNode>.None, Handle<TreeNode>.None);
        var root1 = this.AllocNode(shared, Handle<TreeNode>.None);
        var root2 = this.AllocNode(Handle<TreeNode>.None, shared);

        var slice = new SnapshotSlice<TreeNode, TreeNodeOps>(_store, new UnsafeList<Handle<TreeNode>>());
        slice.AddRoot(root1);
        slice.AddRoot(root2);
        slice.IncrementRootRefCounts();

        Assert.Equal(2, _store.RefCounts.GetCount(shared));

        slice.Release();

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
        var queue = new Queue<SnapshotSlice<TreeNode, TreeNodeOps>>();

        var initialSlice = new SnapshotSlice<TreeNode, TreeNodeOps>(_store, new UnsafeList<Handle<TreeNode>>());
        initialSlice.AddRoot(root);
        initialSlice.IncrementRootRefCounts();
        queue.Enqueue(initialSlice);

        for (var i = 0; i < 30; i++)
        {
            var newRoot = this.PathCopyLeftSpine(root, 3);
            var newSlice = new SnapshotSlice<TreeNode, TreeNodeOps>(_store, new UnsafeList<Handle<TreeNode>>());
            newSlice.AddRoot(newRoot);
            newSlice.IncrementRootRefCounts();
            queue.Enqueue(newSlice);
            root = newRoot;

            while (queue.Count > windowSize)
            {
                var oldSlice = queue.Dequeue();
                oldSlice.Release();
            }
        }

        // Drain remaining
        while (queue.Count > 0)
        {
            var oldSlice = queue.Dequeue();
            oldSlice.Release();
        }

        this.AssertNodeDead(root);
    }

    // ── Re-rooting (internal node promoted to root across snapshots) ─────

    /// <summary>
    /// Snapshot 1 roots A, where A→B→{C,D}. Snapshot 2 roots only B (an internal node from
    /// snapshot 1's graph, not a new allocation). Releasing snapshot 1 frees A but B/C/D survive
    /// because snapshot 2 still pins B. Releasing snapshot 2 then frees B/C/D.
    ///
    /// This verifies that an existing internal node can become a root in a later snapshot, and
    /// that overlapping root pins from different snapshots compose correctly via refcounting.
    /// </summary>
    [Fact]
    public void Reroot_InternalNodeBecomesRoot_SurvivesOldSnapshotRelease()
    {
        var c = this.AllocNode(Handle<TreeNode>.None, Handle<TreeNode>.None);
        var d = this.AllocNode(Handle<TreeNode>.None, Handle<TreeNode>.None);
        var b = this.AllocNode(c, d);
        var a = this.AllocNode(b, Handle<TreeNode>.None);

        // Snapshot 1: root = A
        var snap1 = new SnapshotSlice<TreeNode, TreeNodeOps>(_store, new UnsafeList<Handle<TreeNode>>());
        snap1.AddRoot(a);
        snap1.IncrementRootRefCounts();

        Assert.Equal(1, _store.RefCounts.GetCount(a)); // root pin
        Assert.Equal(1, _store.RefCounts.GetCount(b)); // from A.Left
        Assert.Equal(1, _store.RefCounts.GetCount(c)); // from B.Left
        Assert.Equal(1, _store.RefCounts.GetCount(d)); // from B.Right

        // Snapshot 2: root = B (existing internal node, not A)
        var snap2 = new SnapshotSlice<TreeNode, TreeNodeOps>(_store, new UnsafeList<Handle<TreeNode>>());
        snap2.AddRoot(b);
        snap2.IncrementRootRefCounts();

        Assert.Equal(1, _store.RefCounts.GetCount(a)); // unchanged
        Assert.Equal(2, _store.RefCounts.GetCount(b)); // A.Left + snap2 root pin

        // Release snapshot 1 — A freed, B survives via snap2 pin
        snap1.Release();

        this.AssertNodeDead(a);
        Assert.Equal(1, _store.RefCounts.GetCount(b)); // snap2 root pin only
        this.AssertNodeAlive(c);
        this.AssertNodeAlive(d);

        // Release snapshot 2 — B/C/D all freed
        snap2.Release();

        this.AssertNodeDead(b);
        this.AssertNodeDead(c);
        this.AssertNodeDead(d);
    }

    /// <summary>
    /// Verifies that roots are per-snapshot and NOT inherited. Snapshot 1 pins A, snapshot 2 pins
    /// B (a different node). Each snapshot's root set is independent — releasing one does not affect
    /// the other's ability to keep its subgraph alive.
    /// </summary>
    [Fact]
    public void Roots_ArePerSnapshot_NotInherited()
    {
        var c = this.AllocNode(Handle<TreeNode>.None, Handle<TreeNode>.None);
        var b = this.AllocNode(c, Handle<TreeNode>.None);
        var a = this.AllocNode(b, Handle<TreeNode>.None);

        // Snapshot 1: root = A (graph: A→B→C)
        var snap1 = new SnapshotSlice<TreeNode, TreeNodeOps>(_store, new UnsafeList<Handle<TreeNode>>());
        snap1.AddRoot(a);
        snap1.IncrementRootRefCounts();

        // Snapshot 2: root = B only — does NOT inherit A from snap1
        var snap2 = new SnapshotSlice<TreeNode, TreeNodeOps>(_store, new UnsafeList<Handle<TreeNode>>());
        snap2.AddRoot(b);
        snap2.IncrementRootRefCounts();

        Assert.Equal(1, snap1.Count);
        Assert.Equal(1, snap2.Count);
        Assert.Equal(a, snap1.Roots[0]);
        Assert.Equal(b, snap2.Roots[0]);

        // Release snap2 first: B loses snap2 pin (B: 2→1, still held by A.Left)
        snap2.Release();

        this.AssertNodeAlive(a); // snap1 still pins A
        this.AssertNodeAlive(b); // still referenced by A.Left
        this.AssertNodeAlive(c); // still referenced by B.Left

        // Release snap1: A freed, cascades to B (1→0), cascades to C (1→0)
        snap1.Release();

        this.AssertNodeDead(a);
        this.AssertNodeDead(b);
        this.AssertNodeDead(c);
    }

    /// <summary>
    /// Three-snapshot chain with progressive re-rooting deeper into the graph:
    /// Snap 1: root=A (A→B→C), Snap 2: root=B, Snap 3: root=C.
    /// Release in order: snap1, snap2, snap3. Each release frees exactly the nodes no longer
    /// pinned by any remaining snapshot.
    /// </summary>
    [Fact]
    public void Reroot_ProgressivelyDeeper_ThreeSnapshots()
    {
        var c = this.AllocNode(Handle<TreeNode>.None, Handle<TreeNode>.None);
        var b = this.AllocNode(c, Handle<TreeNode>.None);
        var a = this.AllocNode(b, Handle<TreeNode>.None);

        var snap1 = new SnapshotSlice<TreeNode, TreeNodeOps>(_store, new UnsafeList<Handle<TreeNode>>());
        snap1.AddRoot(a);
        snap1.IncrementRootRefCounts();

        var snap2 = new SnapshotSlice<TreeNode, TreeNodeOps>(_store, new UnsafeList<Handle<TreeNode>>());
        snap2.AddRoot(b);
        snap2.IncrementRootRefCounts();

        var snap3 = new SnapshotSlice<TreeNode, TreeNodeOps>(_store, new UnsafeList<Handle<TreeNode>>());
        snap3.AddRoot(c);
        snap3.IncrementRootRefCounts();

        // A: RC=1(snap1), B: RC=3(A.Left + snap2 + ... wait)
        // Actually: B: RC=1(from A.Left) + 1(snap2 root) = 2
        // C: RC=1(from B.Left) + 1(snap3 root) = 2
        Assert.Equal(1, _store.RefCounts.GetCount(a));
        Assert.Equal(2, _store.RefCounts.GetCount(b));
        Assert.Equal(2, _store.RefCounts.GetCount(c));

        // Release snap1: A freed, B: 2→1
        snap1.Release();
        this.AssertNodeDead(a);
        Assert.Equal(1, _store.RefCounts.GetCount(b));
        Assert.Equal(2, _store.RefCounts.GetCount(c));

        // Release snap2: B freed, C: 2→1
        snap2.Release();
        this.AssertNodeDead(b);
        Assert.Equal(1, _store.RefCounts.GetCount(c));

        // Release snap3: C freed
        snap3.Release();
        this.AssertNodeDead(c);
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
            var refCounts = new RefCountTable<TreeNode>();
            var store = GlobalNodeStore<TreeNode, TreeNodeOps>.TestAccessor.Create(arena, refCounts);
            store.SetNodeOps(new TreeNodeOps { Store = store });

            var root = BuildPerfectTreeInStore(store, 3);
            var slices = new SnapshotSlice<TreeNode, TreeNodeOps>[4];
            var currentRoot = root;

            for (var i = 0; i < 4; i++)
            {
                if (i > 0)
                    currentRoot = PathCopyLeftSpineInStore(store, currentRoot, 3);

                slices[i] = new SnapshotSlice<TreeNode, TreeNodeOps>(store, new UnsafeList<Handle<TreeNode>>());
                slices[i].AddRoot(currentRoot);
                slices[i].IncrementRootRefCounts();
            }

            foreach (var idx in perm)
                slices[idx].Release();

            Assert.Equal(0, store.RefCounts.GetCount(slices[3].Roots[0]));
        }
    }

    private static Handle<TreeNode> AllocNodeInStore(GlobalNodeStore<TreeNode, TreeNodeOps> store, Handle<TreeNode> left, Handle<TreeNode> right)
    {
        var handle = store.Arena.Allocate();
        store.RefCounts.EnsureCapacity(handle.Index + 1);
        store.Arena[handle] = new TreeNode { Left = left, Right = right };
        if (left.IsValid) store.RefCounts.Increment(left);
        if (right.IsValid) store.RefCounts.Increment(right);
        return handle;
    }

    private static Handle<TreeNode> BuildPerfectTreeInStore(GlobalNodeStore<TreeNode, TreeNodeOps> store, int depth)
    {
        if (depth == 0)
            return AllocNodeInStore(store, Handle<TreeNode>.None, Handle<TreeNode>.None);

        var left = BuildPerfectTreeInStore(store, depth - 1);
        var right = BuildPerfectTreeInStore(store, depth - 1);
        return AllocNodeInStore(store, left, right);
    }

    private static Handle<TreeNode> PathCopyLeftSpineInStore(GlobalNodeStore<TreeNode, TreeNodeOps> store, Handle<TreeNode> oldRoot, int depth)
    {
        if (depth == 0)
            return AllocNodeInStore(store, Handle<TreeNode>.None, Handle<TreeNode>.None);

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
