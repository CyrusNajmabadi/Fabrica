using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

/// <summary>
/// End-to-end tests for the full arena coordinator pipeline exercising persistent (immutable, structurally-shared)
/// tree operations: build via buffer, merge into global arena, path-copy to create new versions, release old
/// versions, and verify correct reference counting throughout.
///
/// Uses <see cref="PersistentTreeContext"/> to programmatically build and verify trees rather than hand-coding each
/// permutation.
/// </summary>
public class CoordinatorPersistentTreeTests
{
    // ── Node type ────────────────────────────────────────────────────────

    private struct TreeNode : IArenaNode
    {
        public int Left { get; set; }
        public int Right { get; set; }
        public int Value { get; set; }

        public void FixupReferences(ReadOnlySpan<int> localToGlobalMap)
        {
            if (ArenaIndex.IsLocal(this.Left))
                this.Left = localToGlobalMap[ArenaIndex.UntagLocal(this.Left)];
            if (ArenaIndex.IsLocal(this.Right))
                this.Right = localToGlobalMap[ArenaIndex.UntagLocal(this.Right)];
        }

        public readonly void IncrementChildren(RefCountTable table)
        {
            if (this.Left != ArenaIndex.NoChild)
                table.Increment(this.Left);
            if (this.Right != ArenaIndex.NoChild)
                table.Increment(this.Right);
        }
    }

    private readonly struct TreeHandler(UnsafeSlabArena<TreeNode> arena, List<int>? freed = null) : RefCountTable.IRefCountHandler
    {
        public readonly void OnFreed(int index, RefCountTable table)
        {
            freed?.Add(index);
            var node = arena[index];
            if (node.Left != ArenaIndex.NoChild)
                table.Decrement(node.Left, this);
            if (node.Right != ArenaIndex.NoChild)
                table.Decrement(node.Right, this);
            arena.Free(index);
        }
    }

    // ── Test harness ────────────────────────────────────────────────────

    /// <summary>
    /// Encapsulates coordinator + arena + refcounts and provides helpers for building and path-copying
    /// persistent binary trees through the coordinator pipeline.
    /// </summary>
    private sealed class PersistentTreeContext
    {
        public readonly UnsafeSlabArena<TreeNode> Arena;
        public readonly RefCountTable RefCounts;
        public readonly ArenaCoordinator<TreeNode> Coordinator;
        private readonly List<int> _freed = [];

        private readonly List<int> _liveRoots = [];

        public PersistentTreeContext(int directoryLength = 128, int slabShift = 4)
        {
            Arena = new UnsafeSlabArena<TreeNode>(directoryLength, slabShift);
            RefCounts = new RefCountTable(directoryLength, slabShift);
            Coordinator = new ArenaCoordinator<TreeNode>(Arena, RefCounts);
        }

        public IReadOnlyList<int> Freed => _freed;
        public IReadOnlyList<int> LiveRoots => _liveRoots;

        public TreeHandler CreateHandler()
            => new(Arena, _freed);

        /// <summary>
        /// Builds a complete binary tree of the given depth through the coordinator pipeline.
        /// Returns the global indices in heap-order (index 0 = root, 2i+1 = left child, 2i+2 = right).
        /// </summary>
        public int[] BuildTree(int depth)
        {
            var nodeCount = (1 << (depth + 1)) - 1;
            var buffer = new ThreadLocalBuffer<TreeNode>();
            var localIndices = new int[nodeCount];

            // Build bottom-up: leaves first, then internal nodes.
            for (var pos = nodeCount - 1; pos >= 0; pos--)
            {
                var leftPos = (2 * pos) + 1;
                var rightPos = (2 * pos) + 2;

                var left = leftPos < nodeCount ? ArenaIndex.TagLocal(localIndices[leftPos]) : ArenaIndex.NoChild;
                var right = rightPos < nodeCount ? ArenaIndex.TagLocal(localIndices[rightPos]) : ArenaIndex.NoChild;

                localIndices[pos] = buffer.Append(new TreeNode { Value = pos, Left = left, Right = right });
            }

            Coordinator.MergeBuffer(buffer);

            var globalIndices = new int[nodeCount];
            for (var i = 0; i < nodeCount; i++)
                globalIndices[i] = Coordinator.GetGlobalIndex(localIndices[i]);

            return globalIndices;
        }

        /// <summary>
        /// Registers a global index as a live root (increments its refcount).
        /// </summary>
        public void AddRoot(int globalIndex)
        {
            RefCounts.Increment(globalIndex);
            _liveRoots.Add(globalIndex);
        }

        /// <summary>
        /// Releases a live root through the coordinator pipeline.
        /// </summary>
        public void ReleaseRoot(int globalIndex)
        {
            var buffer = new ThreadLocalBuffer<TreeNode>();
            buffer.LogRelease(globalIndex);
            Coordinator.ProcessReleases([buffer], this.CreateHandler());
            _liveRoots.Remove(globalIndex);
        }

        /// <summary>
        /// Path-copies a tree to change a specific leaf position. Returns the new tree's global indices
        /// in heap-order (same convention as <see cref="BuildTree"/>). The new root is NOT automatically
        /// registered as a live root — caller must call <see cref="AddRoot"/>.
        /// </summary>
        public int[] PathCopy(int[] tree, int targetPosition, int newValue)
        {
            Assert.True(targetPosition >= 0 && targetPosition < tree.Length);

            // Compute path from root to target.
            var path = new List<int>();
            var pos = targetPosition;
            while (pos > 0)
            {
                path.Add(pos);
                pos = (pos - 1) / 2;
            }

            path.Add(0);
            path.Reverse();

            var newTree = (int[])tree.Clone();
            var buffer = new ThreadLocalBuffer<TreeNode>();

            // Create new nodes for the spine, from target to root. Processing in reverse ensures
            // that when we create a node, its children on the path already exist in localIndices.
            var localIndices = new Dictionary<int, int>();
            for (var pi = path.Count - 1; pi >= 0; pi--)
            {
                var p = path[pi];
                var leftPos = (2 * p) + 1;
                var rightPos = (2 * p) + 2;

                int left;
                if (leftPos >= tree.Length)
                    left = ArenaIndex.NoChild;
                else if (localIndices.TryGetValue(leftPos, out var localLeft))
                    left = ArenaIndex.TagLocal(localLeft);
                else
                    left = tree[leftPos];

                int right;
                if (rightPos >= tree.Length)
                    right = ArenaIndex.NoChild;
                else if (localIndices.TryGetValue(rightPos, out var localRight))
                    right = ArenaIndex.TagLocal(localRight);
                else
                    right = tree[rightPos];

                var value = p == targetPosition ? newValue : Arena[tree[p]].Value;
                localIndices[p] = buffer.Append(new TreeNode { Value = value, Left = left, Right = right });
            }

            Coordinator.MergeBuffer(buffer);

            foreach (var p in path)
                newTree[p] = Coordinator.GetGlobalIndex(localIndices[p]);

            return newTree;
        }

        /// <summary>
        /// Verifies that all live roots have refcount >= 1 and that the total refcount state is consistent:
        /// for every node reachable from a live root, its refcount equals the number of parent nodes pointing to it
        /// across all live roots.
        /// </summary>
        public void Verify()
        {
            var expectedRefCounts = new Dictionary<int, int>();

            foreach (var rootIdx in _liveRoots)
            {
                expectedRefCounts.TryAdd(rootIdx, 0);
                expectedRefCounts[rootIdx]++;
            }

            // Walk all reachable nodes, visiting each node exactly once. For each visited node, count
            // its parent→child edges. Shared nodes are visited once, so their children get incremented
            // once — matching the actual refcount (one parent edge per unique parent node).
            var visited = new HashSet<int>();
            foreach (var rootIdx in _liveRoots)
                this.WalkTree(rootIdx, expectedRefCounts, visited);

            foreach (var (index, expected) in expectedRefCounts)
                Assert.Equal(expected, RefCounts.GetCount(index));
        }

        private void WalkTree(int index, Dictionary<int, int> refCounts, HashSet<int> visited)
        {
            if (!visited.Add(index))
                return;

            var node = Arena[index];

            if (node.Left != ArenaIndex.NoChild)
            {
                refCounts.TryAdd(node.Left, 0);
                refCounts[node.Left]++;
                this.WalkTree(node.Left, refCounts, visited);
            }

            if (node.Right != ArenaIndex.NoChild)
            {
                refCounts.TryAdd(node.Right, 0);
                refCounts[node.Right]++;
                this.WalkTree(node.Right, refCounts, visited);
            }
        }
    }

    // ── Permutation helpers ─────────────────────────────────────────────

    private static IEnumerable<int[]> AllPermutations(int n)
    {
        var items = Enumerable.Range(0, n).ToArray();
        return Permute(items, 0);
    }

    private static IEnumerable<int[]> Permute(int[] arr, int start)
    {
        if (start == arr.Length - 1)
        {
            yield return (int[])arr.Clone();
            yield break;
        }

        for (var i = start; i < arr.Length; i++)
        {
            (arr[start], arr[i]) = (arr[i], arr[start]);
            foreach (var perm in Permute(arr, start + 1))
                yield return perm;
            (arr[start], arr[i]) = (arr[i], arr[start]);
        }
    }

    private static IEnumerable<int[]> SampledPermutations(int n, int count, int seed)
    {
        var rng = new Random(seed);
        var items = Enumerable.Range(0, n).ToArray();

        for (var s = 0; s < count; s++)
        {
            var perm = (int[])items.Clone();
            for (var i = perm.Length - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (perm[i], perm[j]) = (perm[j], perm[i]);
            }

            yield return perm;
        }
    }

    // ═══════════════════════════ Single tree: build + verify ═════════════

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void BuildTree_CorrectRefcounts(int depth)
    {
        var ctx = new PersistentTreeContext();
        var tree = ctx.BuildTree(depth);
        ctx.AddRoot(tree[0]);
        ctx.Verify();
    }

    // ═══════════════════════════ Single path-copy ════════════════════════

    [Theory]
    [InlineData(2, 3)]
    [InlineData(2, 4)]
    [InlineData(2, 5)]
    [InlineData(2, 6)]
    [InlineData(3, 7)]
    [InlineData(3, 10)]
    [InlineData(3, 14)]
    public void PathCopy_OneVersion_CorrectRefcounts(int depth, int targetLeaf)
    {
        var ctx = new PersistentTreeContext();
        var v1 = ctx.BuildTree(depth);
        ctx.AddRoot(v1[0]);

        var v2 = ctx.PathCopy(v1, targetLeaf, 999);
        ctx.AddRoot(v2[0]);

        ctx.Verify();
    }

    // ═══════════════════════════ Fork + release old = only old spine freed

    [Theory]
    [InlineData(2, 3)]
    [InlineData(2, 6)]
    [InlineData(3, 7)]
    [InlineData(3, 14)]
    public void PathCopy_ReleaseOld_FreesOnlyOldSpine(int depth, int targetLeaf)
    {
        var ctx = new PersistentTreeContext();
        var v1 = ctx.BuildTree(depth);
        ctx.AddRoot(v1[0]);

        var v2 = ctx.PathCopy(v1, targetLeaf, 999);
        ctx.AddRoot(v2[0]);

        ctx.ReleaseRoot(v1[0]);
        ctx.Verify();

        // Compute expected freed count = path length from root to target.
        var pathLength = 1;
        var pos = targetLeaf;
        while (pos > 0)
        {
            pathLength++;
            pos = (pos - 1) / 2;
        }

        Assert.Equal(pathLength, ctx.Freed.Count);
    }

    // ═══════════════════════════ 4 versions, all 4! release orders ═══════

    [Theory]
    [MemberData(nameof(FourVersionReleaseOrders))]
    public void FourVersions_AllReleaseOrders(int[] releaseOrder)
    {
        var ctx = new PersistentTreeContext();
        var v1 = ctx.BuildTree(3);
        ctx.AddRoot(v1[0]);

        var v2 = ctx.PathCopy(v1, 7, 100);
        ctx.AddRoot(v2[0]);

        var v3 = ctx.PathCopy(v2, 10, 200);
        ctx.AddRoot(v3[0]);

        var v4 = ctx.PathCopy(v3, 14, 300);
        ctx.AddRoot(v4[0]);

        var roots = new[] { v1[0], v2[0], v3[0], v4[0] };

        for (var i = 0; i < releaseOrder.Length; i++)
        {
            ctx.ReleaseRoot(roots[releaseOrder[i]]);
            if (i < releaseOrder.Length - 1)
                ctx.Verify();
        }

        // After releasing all roots, every allocated node should be freed.
        var allGlobals = new HashSet<int>();
        foreach (var idx in v1) allGlobals.Add(idx);
        foreach (var idx in v2) allGlobals.Add(idx);
        foreach (var idx in v3) allGlobals.Add(idx);
        foreach (var idx in v4) allGlobals.Add(idx);

        foreach (var g in allGlobals)
            Assert.Equal(0, ctx.RefCounts.GetCount(g));
    }

    public static IEnumerable<object[]> FourVersionReleaseOrders()
        => AllPermutations(4).Select(p => new object[] { p });

    // ═══════════════════════════ 5 versions + interior root, all 5! ═════

    [Theory]
    [MemberData(nameof(FiveVersionReleaseOrders))]
    public void FiveVersions_WithInteriorRoot_AllReleaseOrders(int[] releaseOrder)
    {
        var ctx = new PersistentTreeContext();
        var v1 = ctx.BuildTree(3);
        ctx.AddRoot(v1[0]);

        var v2 = ctx.PathCopy(v1, 7, 100);
        ctx.AddRoot(v2[0]);

        var v3 = ctx.PathCopy(v2, 14, 200);
        ctx.AddRoot(v3[0]);

        // Also add an interior root pointing to a shared subtree.
        ctx.AddRoot(v1[2]);

        var v4 = ctx.PathCopy(v1, 10, 300);
        ctx.AddRoot(v4[0]);

        var roots = new[] { v1[0], v2[0], v3[0], v1[2], v4[0] };

        for (var i = 0; i < releaseOrder.Length; i++)
        {
            ctx.ReleaseRoot(roots[releaseOrder[i]]);
            if (i < releaseOrder.Length - 1)
                ctx.Verify();
        }

        // All unique global indices should have refcount 0.
        var allGlobals = new HashSet<int>();
        foreach (var idx in v1) allGlobals.Add(idx);
        foreach (var idx in v2) allGlobals.Add(idx);
        foreach (var idx in v3) allGlobals.Add(idx);
        foreach (var idx in v4) allGlobals.Add(idx);

        foreach (var g in allGlobals)
            Assert.Equal(0, ctx.RefCounts.GetCount(g));
    }

    public static IEnumerable<object[]> FiveVersionReleaseOrders()
        => AllPermutations(5).Select(p => new object[] { p });

    // ═══════════════════════════ Chained path-copies, all 4! ════════════

    [Theory]
    [MemberData(nameof(ChainedPathCopyOrders))]
    public void ChainedPathCopies_AllReleaseOrders(int[] releaseOrder)
    {
        var ctx = new PersistentTreeContext();
        var v1 = ctx.BuildTree(3);
        ctx.AddRoot(v1[0]);

        // V2 forks from V1, V3 from V2, V4 from V3 (chain).
        var v2 = ctx.PathCopy(v1, 7, 100);
        ctx.AddRoot(v2[0]);

        var v3 = ctx.PathCopy(v2, 10, 200);
        ctx.AddRoot(v3[0]);

        var v4 = ctx.PathCopy(v3, 14, 300);
        ctx.AddRoot(v4[0]);

        var roots = new[] { v1[0], v2[0], v3[0], v4[0] };

        for (var i = 0; i < releaseOrder.Length; i++)
        {
            ctx.ReleaseRoot(roots[releaseOrder[i]]);
            if (i < releaseOrder.Length - 1)
                ctx.Verify();
        }

        var allGlobals = new HashSet<int>();
        foreach (var idx in v1) allGlobals.Add(idx);
        foreach (var idx in v2) allGlobals.Add(idx);
        foreach (var idx in v3) allGlobals.Add(idx);
        foreach (var idx in v4) allGlobals.Add(idx);

        foreach (var g in allGlobals)
            Assert.Equal(0, ctx.RefCounts.GetCount(g));
    }

    public static IEnumerable<object[]> ChainedPathCopyOrders()
        => AllPermutations(4).Select(p => new object[] { p });

    // ═══════════════════════════ Large tree, sampled release orders ═════

    [Theory]
    [MemberData(nameof(LargeTreeSampledOrders))]
    public void Depth4_SixVersions_SampledReleaseOrders(int[] releaseOrder)
    {
        var ctx = new PersistentTreeContext(directoryLength: 256, slabShift: 4);
        var v1 = ctx.BuildTree(4);
        ctx.AddRoot(v1[0]);

        var v2 = ctx.PathCopy(v1, 15, 100);
        ctx.AddRoot(v2[0]);

        var v3 = ctx.PathCopy(v2, 22, 200);
        ctx.AddRoot(v3[0]);

        var v4 = ctx.PathCopy(v3, 30, 300);
        ctx.AddRoot(v4[0]);

        var v5 = ctx.PathCopy(v1, 18, 400);
        ctx.AddRoot(v5[0]);

        // Interior root.
        ctx.AddRoot(v1[6]);

        var roots = new[] { v1[0], v2[0], v3[0], v4[0], v5[0], v1[6] };

        for (var i = 0; i < releaseOrder.Length; i++)
        {
            ctx.ReleaseRoot(roots[releaseOrder[i]]);
            if (i < releaseOrder.Length - 1)
                ctx.Verify();
        }

        var allGlobals = new HashSet<int>();
        foreach (var tree in new[] { v1, v2, v3, v4, v5 })
            foreach (var idx in tree)
                allGlobals.Add(idx);

        foreach (var g in allGlobals)
            Assert.Equal(0, ctx.RefCounts.GetCount(g));
    }

    public static IEnumerable<object[]> LargeTreeSampledOrders()
        => SampledPermutations(6, 50, seed: 42).Select(p => new object[] { p });

    // ═══════════════════════════ Interleaved add + release ═══════════════

    [Fact]
    public void InterleavedAddAndRelease()
    {
        var ctx = new PersistentTreeContext();
        var v1 = ctx.BuildTree(3);
        ctx.AddRoot(v1[0]);
        ctx.Verify();

        var v2 = ctx.PathCopy(v1, 7, 100);
        ctx.AddRoot(v2[0]);
        ctx.Verify();

        // Release v1 before creating v3.
        ctx.ReleaseRoot(v1[0]);
        ctx.Verify();

        var v3 = ctx.PathCopy(v2, 14, 200);
        ctx.AddRoot(v3[0]);
        ctx.Verify();

        // Add a root to a subtree, then release v2.
        ctx.AddRoot(v2[2]);
        ctx.ReleaseRoot(v2[0]);
        ctx.Verify();

        // Now v3 and the subtree root (v2[2]) are alive.
        ctx.ReleaseRoot(v2[2]);
        ctx.Verify();

        ctx.ReleaseRoot(v3[0]);

        // Everything freed.
        var allGlobals = new HashSet<int>();
        foreach (var idx in v1) allGlobals.Add(idx);
        foreach (var idx in v2) allGlobals.Add(idx);
        foreach (var idx in v3) allGlobals.Add(idx);

        foreach (var g in allGlobals)
            Assert.Equal(0, ctx.RefCounts.GetCount(g));
    }

    // ═══════════════════════════ Multi-buffer: two workers ═══════════════

    [Fact]
    public void TwoBuffers_SimultaneousPathCopies()
    {
        var ctx = new PersistentTreeContext();
        var v1 = ctx.BuildTree(2);
        ctx.AddRoot(v1[0]);

        // Worker 1: path-copy leaf 3.
        var buf1 = new ThreadLocalBuffer<TreeNode>();
        {
            var newLeaf = buf1.Append(new TreeNode { Value = 30, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });
            var newMid = buf1.Append(new TreeNode
            {
                Value = 10,
                Left = ArenaIndex.TagLocal(newLeaf),
                Right = v1[2],
            });
            buf1.Append(new TreeNode
            {
                Value = 100,
                Left = ArenaIndex.TagLocal(newMid),
                Right = v1[2],
            });
        }

        // Worker 2: path-copy leaf 5.
        var buf2 = new ThreadLocalBuffer<TreeNode>();
        {
            var newLeaf = buf2.Append(new TreeNode { Value = 50, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });
            var newMid = buf2.Append(new TreeNode
            {
                Value = 20,
                Left = v1[5],
                Right = ArenaIndex.TagLocal(newLeaf),
            });
            buf2.Append(new TreeNode
            {
                Value = 200,
                Left = v1[1],
                Right = ArenaIndex.TagLocal(newMid),
            });
        }

        ctx.Coordinator.MergeBuffer(buf1);
        var v2Root = ctx.Coordinator.GetGlobalIndex(2);

        ctx.Coordinator.MergeBuffer(buf2);
        var v3Root = ctx.Coordinator.GetGlobalIndex(2);

        ctx.AddRoot(v2Root);
        ctx.AddRoot(v3Root);
        ctx.Verify();

        // Release original.
        ctx.ReleaseRoot(v1[0]);
        ctx.Verify();

        // Release both new versions.
        ctx.ReleaseRoot(v2Root);
        ctx.Verify();

        ctx.ReleaseRoot(v3Root);

        // All freed.
        var allGlobals = new HashSet<int>(v1)
        {
            v2Root,
            ctx.Coordinator.GetGlobalIndex(0),
            ctx.Coordinator.GetGlobalIndex(1)
        };

        foreach (var g in allGlobals)
            Assert.Equal(0, ctx.RefCounts.GetCount(g));
    }

    // ═══════════════════════════ Stress: many versions ═══════════════════

    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    public void ManyVersions_SteadyState(int versionCount)
    {
        var ctx = new PersistentTreeContext(directoryLength: 256, slabShift: 4);
        var rng = new Random(42);

        var tree = ctx.BuildTree(3);
        ctx.AddRoot(tree[0]);

        var nodeCount = tree.Length;
        var leafStart = nodeCount / 2;
        var leafEnd = nodeCount - 1;

        var roots = new List<(int rootGlobal, int[] treeIndices)>
        {
            (tree[0], tree),
        };

        for (var v = 0; v < versionCount; v++)
        {
            var (rootGlobal, treeIndices) = roots[rng.Next(roots.Count)];
            var targetLeaf = rng.Next(leafStart, leafEnd + 1);
            var newTree = ctx.PathCopy(treeIndices, targetLeaf, v + 1000);
            ctx.AddRoot(newTree[0]);
            roots.Add((newTree[0], newTree));

            // Periodically release old roots.
            if (roots.Count > 5)
            {
                var releaseIdx = rng.Next(roots.Count);
                ctx.ReleaseRoot(roots[releaseIdx].rootGlobal);
                roots.RemoveAt(releaseIdx);
            }

            if (v % 50 == 0)
                ctx.Verify();
        }

        ctx.Verify();

        // Release everything.
        while (roots.Count > 0)
        {
            ctx.ReleaseRoot(roots[^1].rootGlobal);
            roots.RemoveAt(roots.Count - 1);
        }
    }
}
