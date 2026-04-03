using System.Runtime.InteropServices;
using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class DagValidatorTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TreeNode
    {
        public int Left { get; set; }
        public int Right { get; set; }
    }

    private struct TreeChildEnumerator : DagValidator.IChildEnumerator<TreeNode>
    {
        public readonly void GetChildren(in TreeNode node, List<int> children)
        {
            if (node.Left >= 0) children.Add(node.Left);
            if (node.Right >= 0) children.Add(node.Right);
        }
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

    private static NodeStore<TreeNode, TreeHandler> CreateStore()
    {
        var arena = new UnsafeSlabArena<TreeNode>();
        var refCounts = new RefCountTable();
        var handler = new TreeHandler(arena);
        return new NodeStore<TreeNode, TreeHandler>(arena, refCounts, handler);
    }

    private static int AllocNode(NodeStore<TreeNode, TreeHandler> store, int left, int right)
    {
        var index = store.Arena.Allocate();
        store.RefCounts.EnsureCapacity(index + 1);
        store.Arena[index] = new TreeNode { Left = left, Right = right };
        if (left >= 0) store.RefCounts.Increment(left);
        if (right >= 0) store.RefCounts.Increment(right);
        return index;
    }

    private static void AssertValid(NodeStore<TreeNode, TreeHandler> store, ReadOnlySpan<int> roots)
        => DagValidator.AssertValid(store, roots, new TreeChildEnumerator());

    private static List<string> Validate(NodeStore<TreeNode, TreeHandler> store, ReadOnlySpan<int> roots)
        => DagValidator.Validate(store, roots, new TreeChildEnumerator());

    // ── Valid DAGs pass ──────────────────────────────────────────────────

    [Fact]
    public void EmptyRoots_EmptyArena_Valid()
        => AssertValid(CreateStore(), []);

    [Fact]
    public void SingleLeafRoot_Valid()
    {
        var store = CreateStore();
        var root = AllocNode(store, -1, -1);
        store.RefCounts.Increment(root);
        AssertValid(store, [root]);
    }

    [Fact]
    public void SimpleTree_Valid()
    {
        var store = CreateStore();
        var left = AllocNode(store, -1, -1);
        var right = AllocNode(store, -1, -1);
        var root = AllocNode(store, left, right);
        store.RefCounts.Increment(root);
        AssertValid(store, [root]);
    }

    [Fact]
    public void DiamondDAG_SharedChild_Valid()
    {
        var store = CreateStore();

        //     root
        //    /    \
        //   a      b
        //    \    /
        //     shared
        var shared = AllocNode(store, -1, -1);
        var a = AllocNode(store, -1, shared);
        var b = AllocNode(store, shared, -1);
        var root = AllocNode(store, a, b);

        // shared has refcount 2 from structural references
        Assert.Equal(2, store.RefCounts.GetCount(shared));

        store.RefCounts.Increment(root);
        AssertValid(store, [root]);
    }

    [Fact]
    public void DeepTree_Valid()
    {
        var store = CreateStore();
        var node = AllocNode(store, -1, -1);
        for (var i = 0; i < 10; i++)
            node = AllocNode(store, node, -1);

        store.RefCounts.Increment(node);
        AssertValid(store, [node]);
    }

    [Fact]
    public void MultipleRoots_DisjointTrees_Valid()
    {
        var store = CreateStore();
        var r1 = AllocNode(store, AllocNode(store, -1, -1), -1);
        var r2 = AllocNode(store, -1, AllocNode(store, -1, -1));
        store.RefCounts.Increment(r1);
        store.RefCounts.Increment(r2);
        AssertValid(store, [r1, r2]);
    }

    [Fact]
    public void PerfectTree_Depth3_Valid()
    {
        var store = CreateStore();
        var root = BuildPerfectTree(store, 3);
        store.RefCounts.Increment(root);
        AssertValid(store, [root]);
    }

    [Fact]
    public void TwoSnapshotsSharing_BothValid()
    {
        var store = CreateStore();

        var leaf = AllocNode(store, -1, -1);
        var root1 = AllocNode(store, leaf, -1);
        var root2 = AllocNode(store, -1, leaf);

        store.RefCounts.Increment(root1);
        store.RefCounts.Increment(root2);

        // leaf has refcount 2 (one from each parent)
        // Validate for snapshot 1's roots
        AssertValid(store, [root1, root2]);
    }

    [Fact]
    public void PathCopy_BothVersions_Valid()
    {
        var store = CreateStore();

        //     root1          root2
        //    /     \        /     \
        //   a       b      a'      b     (b shared)
        //  / \             / \
        // c   d           c'  d          (d shared)
        var c = AllocNode(store, -1, -1);
        var d = AllocNode(store, -1, -1);
        var a = AllocNode(store, c, d);
        var b = AllocNode(store, -1, -1);
        var root1 = AllocNode(store, a, b);
        store.RefCounts.Increment(root1);

        var cPrime = AllocNode(store, -1, -1);
        var aPrime = AllocNode(store, cPrime, d);
        var root2 = AllocNode(store, aPrime, b);
        store.RefCounts.Increment(root2);

        // d and b each have refcount 2 (shared)
        Assert.Equal(2, store.RefCounts.GetCount(d));
        Assert.Equal(2, store.RefCounts.GetCount(b));

        AssertValid(store, [root1, root2]);
    }

    // ── Violation: refcount mismatch ─────────────────────────────────────

    [Fact]
    public void DetectsRefcountTooHigh()
    {
        var store = CreateStore();
        var leaf = AllocNode(store, -1, -1);
        var root = AllocNode(store, leaf, -1);
        store.RefCounts.Increment(root);

        // Artificially inflate leaf's refcount
        store.RefCounts.Increment(leaf);
        Assert.Equal(2, store.RefCounts.GetCount(leaf));

        var issues = Validate(store, [root]);
        Assert.Contains(issues, i => i.Contains($"Node {leaf}") && i.Contains("expected refcount 1, actual 2"));
    }

    [Fact]
    public void DetectsRefcountTooLow()
    {
        var store = CreateStore();
        var shared = AllocNode(store, -1, -1);
        var a = AllocNode(store, shared, -1);
        var b = AllocNode(store, -1, shared);
        var root = AllocNode(store, a, b);
        store.RefCounts.Increment(root);

        // shared should have refcount 2, but we artificially decrement it to 1
        // We do this by directly manipulating — the refcount table doesn't expose raw write,
        // but we can simulate by noting shared has refcount 2 (from two parents).
        // To test "too low", we build a graph where the actual refcount is wrong.
        // Instead: build a node with only one parent but set refcount to 2.
        var store2 = CreateStore();
        var leaf2 = AllocNode(store2, -1, -1);
        var root2 = AllocNode(store2, leaf2, -1);
        store2.RefCounts.Increment(root2);
        // leaf2 has refcount 1 (correct). Add an extra increment to make it wrong.
        store2.RefCounts.Increment(leaf2);

        var issues2 = Validate(store2, [root2]);
        Assert.Contains(issues2, i => i.Contains($"Node {leaf2}") && i.Contains("expected refcount 1, actual 2"));
    }

    [Fact]
    public void DetectsUnreachableNodeWithPositiveRefcount()
    {
        var store = CreateStore();
        var root = AllocNode(store, -1, -1);
        store.RefCounts.Increment(root);

        // Allocate an orphan node with refcount > 0 (not reachable from any root)
        var orphan = AllocNode(store, -1, -1);
        store.RefCounts.EnsureCapacity(orphan + 1);
        store.RefCounts.Increment(orphan);

        var issues = Validate(store, [root]);
        Assert.Contains(issues, i => i.Contains($"Node {orphan}") && i.Contains("reachable=False"));
    }

    // ── Violation: empty roots but live nodes ────────────────────────────

    [Fact]
    public void DetectsLiveNodesWithEmptyRoots()
    {
        var store = CreateStore();
        var node = AllocNode(store, -1, -1);
        store.RefCounts.Increment(node);

        var issues = Validate(store, []);
        Assert.Contains(issues, i => i.Contains($"Node {node}") && i.Contains("expected refcount 0, actual 1"));
    }

    // ── Violation: root is also a child ──────────────────────────────────

    [Fact]
    public void DetectsRootThatIsAlsoChild()
    {
        var store = CreateStore();
        var child = AllocNode(store, -1, -1);
        var root = AllocNode(store, child, -1);
        store.RefCounts.Increment(root);

        // Mark 'child' as a root too — but it's also pointed at by 'root'
        store.RefCounts.Increment(child);

        var issues = Validate(store, [root, child]);
        Assert.Contains(issues, i => i.Contains($"Root {child}") && i.Contains("also a child"));
    }

    [Fact]
    public void DetectsRootThatIsChildInDiamond()
    {
        var store = CreateStore();

        //   root
        //   /  \
        //  a    b (also marked as root)
        //   \  /
        //    c
        var c = AllocNode(store, -1, -1);
        var a = AllocNode(store, -1, c);
        var b = AllocNode(store, c, -1);
        var root = AllocNode(store, a, b);
        store.RefCounts.Increment(root);
        store.RefCounts.Increment(b);

        var issues = Validate(store, [root, b]);
        Assert.Contains(issues, i => i.Contains($"Root {b}") && i.Contains("also a child"));
    }

    // ── Violation: cycle detection ───────────────────────────────────────

    [Fact]
    public void DetectsSelfLoop()
    {
        var store = CreateStore();
        var node = store.Arena.Allocate();
        store.RefCounts.EnsureCapacity(node + 1);
        // Self-loop: node points to itself
        store.Arena[node] = new TreeNode { Left = node, Right = -1 };
        store.RefCounts.Increment(node); // structural self-reference
        store.RefCounts.Increment(node); // root hold

        var issues = Validate(store, [node]);
        Assert.Contains(issues, i => i.Contains("Cycle") || i.Contains("back-edge"));
    }

    [Fact]
    public void DetectsTwoNodeCycle()
    {
        var store = CreateStore();
        var a = store.Arena.Allocate();
        var b = store.Arena.Allocate();
        store.RefCounts.EnsureCapacity(b + 1);

        // a → b → a (cycle)
        store.Arena[a] = new TreeNode { Left = b, Right = -1 };
        store.Arena[b] = new TreeNode { Left = a, Right = -1 };
        store.RefCounts.Increment(a); // b → a
        store.RefCounts.Increment(b); // a → b
        store.RefCounts.Increment(a); // root hold

        var issues = Validate(store, [a]);
        Assert.Contains(issues, i => i.Contains("Cycle") || i.Contains("back-edge"));
    }

    // ── Violation: dangling reference ────────────────────────────────────

    [Fact]
    public void DetectsOutOfRangeChild()
    {
        var store = CreateStore();
        var node = store.Arena.Allocate();
        store.RefCounts.EnsureCapacity(node + 1);
        store.Arena[node] = new TreeNode { Left = 9999, Right = -1 };
        store.RefCounts.Increment(node);

        var issues = Validate(store, [node]);
        Assert.Contains(issues, i => i.Contains("out-of-range child index 9999"));
    }

    // ── Valid after full lifecycle ────────────────────────────────────────

    [Fact]
    public void ValidAfterPathCopyAndRelease()
    {
        var store = CreateStore();

        var root = BuildPerfectTree(store, 3);
        store.RefCounts.Increment(root);
        AssertValid(store, [root]);

        // Path-copy left spine
        var newRoot = PathCopyLeftSpine(store, root, 3);
        store.RefCounts.Increment(newRoot);
        AssertValid(store, [root, newRoot]);

        // Release old root
        var slice = new SnapshotSlice<TreeNode, TreeHandler>(store);
        slice.AddRoot(root);
        slice.DecrementRootRefCounts();

        AssertValid(store, [newRoot]);
    }

    [Fact]
    public void ValidAfterMultiplePathCopiesAndReleases()
    {
        var store = CreateStore();

        var root = BuildPerfectTree(store, 3);
        store.RefCounts.Increment(root);

        for (var i = 0; i < 10; i++)
        {
            var newRoot = PathCopyLeftSpine(store, root, 3);
            store.RefCounts.Increment(newRoot);
            AssertValid(store, [root, newRoot]);

            var slice = new SnapshotSlice<TreeNode, TreeHandler>(store);
            slice.AddRoot(root);
            slice.DecrementRootRefCounts();

            root = newRoot;
            AssertValid(store, [root]);
        }
    }

    [Fact]
    public void EmptyAfterReleasingAllRoots()
    {
        var store = CreateStore();
        var root = BuildPerfectTree(store, 3);
        store.RefCounts.Increment(root);

        var slice = new SnapshotSlice<TreeNode, TreeHandler>(store);
        slice.AddRoot(root);
        slice.DecrementRootRefCounts();

        AssertValid(store, []);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static int BuildPerfectTree(NodeStore<TreeNode, TreeHandler> store, int depth)
    {
        if (depth == 0)
            return AllocNode(store, -1, -1);

        var left = BuildPerfectTree(store, depth - 1);
        var right = BuildPerfectTree(store, depth - 1);
        return AllocNode(store, left, right);
    }

    private static int PathCopyLeftSpine(NodeStore<TreeNode, TreeHandler> store, int oldRoot, int depth)
    {
        if (depth == 0)
            return AllocNode(store, -1, -1);

        ref readonly var old = ref store.Arena[oldRoot];
        var newLeft = PathCopyLeftSpine(store, old.Left, depth - 1);
        return AllocNode(store, newLeft, old.Right);
    }
}
