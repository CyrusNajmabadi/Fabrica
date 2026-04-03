using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class ArenaCoordinatorTests
{
    // ── Test node type ───────────────────────────────────────────────────

    private struct TestNode : IArenaNode
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

    private readonly struct TestHandler(UnsafeSlabArena<TestNode> arena, List<int>? freed = null) : RefCountTable.IRefCountHandler
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

    // ── Factory ─────────────────────────────────────────────────────────

    private static (ArenaCoordinator<TestNode> coordinator, UnsafeSlabArena<TestNode> arena, RefCountTable refCounts) Create(
        int directoryLength = 16, int slabShift = 2)
    {
        var arena = new UnsafeSlabArena<TestNode>(directoryLength, slabShift);
        var refCounts = new RefCountTable(directoryLength, slabShift);
        var coordinator = new ArenaCoordinator<TestNode>(arena, refCounts);
        return (coordinator, arena, refCounts);
    }

    // ═══════════════════════════ MergeBuffer basics ═══════════════════════

    [Fact]
    public void MergeBuffer_EmptyBuffer_IsNoOp()
    {
        var (coordinator, arena, _) = Create();
        var buffer = new ThreadLocalBuffer<TestNode>();

        coordinator.MergeBuffer(buffer);

        Assert.Equal(0, arena.GetTestAccessor().Count);
    }

    [Fact]
    public void MergeBuffer_SingleNode_AllocatesGlobal()
    {
        var (coordinator, arena, refCounts) = Create();
        var buffer = new ThreadLocalBuffer<TestNode>();
        buffer.Append(new TestNode { Value = 42, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });

        coordinator.MergeBuffer(buffer);

        Assert.Equal(1, arena.GetTestAccessor().Count);
        var globalIndex = coordinator.GetGlobalIndex(0);
        Assert.Equal(42, arena[globalIndex].Value);
        Assert.Equal(0, refCounts.GetCount(globalIndex));
    }

    [Fact]
    public void MergeBuffer_MultipleNodes_AllAllocated()
    {
        var (coordinator, arena, _) = Create();
        var buffer = new ThreadLocalBuffer<TestNode>();

        for (var i = 0; i < 10; i++)
            buffer.Append(new TestNode { Value = i, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });

        coordinator.MergeBuffer(buffer);

        Assert.Equal(10, arena.GetTestAccessor().Count);
        for (var i = 0; i < 10; i++)
            Assert.Equal(i, arena[coordinator.GetGlobalIndex(i)].Value);
    }

    // ═══════════════════════════ Reference fixup ═════════════════════════

    [Fact]
    public void MergeBuffer_FixesUpLocalReferences()
    {
        var (coordinator, arena, _) = Create();
        var buffer = new ThreadLocalBuffer<TestNode>();

        var leafIdx = buffer.Append(new TestNode { Value = 1, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });
        buffer.Append(new TestNode { Value = 2, Left = ArenaIndex.TagLocal(leafIdx), Right = ArenaIndex.NoChild });

        coordinator.MergeBuffer(buffer);

        var leafGlobal = coordinator.GetGlobalIndex(0);
        var rootGlobal = coordinator.GetGlobalIndex(1);

        Assert.Equal(leafGlobal, arena[rootGlobal].Left);
        Assert.Equal(ArenaIndex.NoChild, arena[rootGlobal].Right);
    }

    [Fact]
    public void MergeBuffer_FixesUpLocalReferences_Chain()
    {
        var (coordinator, arena, _) = Create();
        var buffer = new ThreadLocalBuffer<TestNode>();

        var aIdx = buffer.Append(new TestNode { Value = 1, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });
        var bIdx = buffer.Append(new TestNode { Value = 2, Left = ArenaIndex.TagLocal(aIdx), Right = ArenaIndex.NoChild });
        buffer.Append(new TestNode { Value = 3, Left = ArenaIndex.TagLocal(bIdx), Right = ArenaIndex.NoChild });

        coordinator.MergeBuffer(buffer);

        var aGlobal = coordinator.GetGlobalIndex(0);
        var bGlobal = coordinator.GetGlobalIndex(1);
        var cGlobal = coordinator.GetGlobalIndex(2);

        Assert.Equal(aGlobal, arena[bGlobal].Left);
        Assert.Equal(bGlobal, arena[cGlobal].Left);
    }

    [Fact]
    public void MergeBuffer_PreservesGlobalReferences()
    {
        var (coordinator, arena, refCounts) = Create();

        // Pre-populate a node in the arena manually.
        var existingIndex = arena.Allocate();
        arena[existingIndex] = new TestNode { Value = 99, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild };
        refCounts.EnsureCapacity(existingIndex + 1);
        refCounts.Increment(existingIndex);

        // Worker creates a new node pointing to the existing global node.
        var buffer = new ThreadLocalBuffer<TestNode>();
        buffer.Append(new TestNode { Value = 1, Left = existingIndex, Right = ArenaIndex.NoChild });

        coordinator.MergeBuffer(buffer);

        var newGlobal = coordinator.GetGlobalIndex(0);
        Assert.Equal(existingIndex, arena[newGlobal].Left);
        Assert.Equal(2, refCounts.GetCount(existingIndex));
    }

    // ═══════════════════════════ Child refcount increments ═══════════════

    [Fact]
    public void MergeBuffer_IncrementsChildRefcounts()
    {
        var (coordinator, _, refCounts) = Create();
        var buffer = new ThreadLocalBuffer<TestNode>();

        var leafIdx = buffer.Append(new TestNode { Value = 1, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });
        buffer.Append(new TestNode { Value = 2, Left = ArenaIndex.TagLocal(leafIdx), Right = ArenaIndex.TagLocal(leafIdx) });

        coordinator.MergeBuffer(buffer);

        var leafGlobal = coordinator.GetGlobalIndex(0);
        Assert.Equal(2, refCounts.GetCount(leafGlobal));
    }

    [Fact]
    public void MergeBuffer_NewNodesStartAtRefcountZero()
    {
        var (coordinator, _, refCounts) = Create();
        var buffer = new ThreadLocalBuffer<TestNode>();

        buffer.Append(new TestNode { Value = 1, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });

        coordinator.MergeBuffer(buffer);

        var global = coordinator.GetGlobalIndex(0);
        Assert.Equal(0, refCounts.GetCount(global));
    }

    // ═══════════════════════════ ProcessReleases ═════════════════════════

    [Fact]
    public void ProcessReleases_DecrementsBatch()
    {
        var (coordinator, arena, refCounts) = Create();

        // Setup: create a root node manually in the arena with refcount 1.
        var rootIndex = arena.Allocate();
        arena[rootIndex] = new TestNode { Value = 1, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild };
        refCounts.EnsureCapacity(rootIndex + 1);
        refCounts.Increment(rootIndex);

        // Worker logs release of that root.
        var buffer = new ThreadLocalBuffer<TestNode>();
        buffer.LogRelease(rootIndex);

        var freed = new List<int>();
        var handler = new TestHandler(arena, freed);
        coordinator.ProcessReleases([buffer], handler);

        Assert.Contains(rootIndex, freed);
        Assert.Equal(0, refCounts.GetCount(rootIndex));
    }

    [Fact]
    public void ProcessReleases_CascadesFreeingChildren()
    {
        var (coordinator, arena, refCounts) = Create();

        // Build a small tree: root -> left, root -> right.
        var left = arena.Allocate();
        var right = arena.Allocate();
        var root = arena.Allocate();
        refCounts.EnsureCapacity(root + 1);

        arena[left] = new TestNode { Value = 1, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild };
        arena[right] = new TestNode { Value = 2, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild };
        arena[root] = new TestNode { Value = 3, Left = left, Right = right };

        refCounts.Increment(left);
        refCounts.Increment(right);
        refCounts.Increment(root);

        // Release root.
        var buffer = new ThreadLocalBuffer<TestNode>();
        buffer.LogRelease(root);

        var freed = new List<int>();
        var handler = new TestHandler(arena, freed);
        coordinator.ProcessReleases([buffer], handler);

        Assert.Equal(3, freed.Count);
        Assert.Contains(root, freed);
        Assert.Contains(left, freed);
        Assert.Contains(right, freed);
    }

    // ═══════════════════════════ Merge convenience ═══════════════════════

    [Fact]
    public void Merge_MergesBufferAndProcessesReleases()
    {
        var (coordinator, arena, refCounts) = Create();

        // Pre-populate a small tree: root -> child.
        var child = arena.Allocate();
        var root = arena.Allocate();
        refCounts.EnsureCapacity(root + 1);
        arena[child] = new TestNode { Value = 1, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild };
        arena[root] = new TestNode { Value = 2, Left = child, Right = ArenaIndex.NoChild };
        refCounts.Increment(child);
        refCounts.Increment(root);

        // Worker creates a new root pointing to the existing child, and releases the old root.
        var buffer = new ThreadLocalBuffer<TestNode>();
        buffer.Append(new TestNode { Value = 3, Left = child, Right = ArenaIndex.NoChild });
        buffer.LogRelease(root);

        var freed = new List<int>();
        var handler = new TestHandler(arena, freed);
        coordinator.Merge([buffer], handler);

        // New root's merge incremented child's refcount (now 2).
        // Old root's release decremented child's refcount (back to 1).
        var newRootGlobal = coordinator.GetGlobalIndex(0);
        Assert.Equal(3, arena[newRootGlobal].Value);
        Assert.Equal(1, refCounts.GetCount(child));

        // Old root was freed.
        Assert.Contains(root, freed);

        // Child was NOT freed (still referenced by new root).
        Assert.DoesNotContain(child, freed);
    }

    // ═══════════════════════════ Multiple buffers ═════════════════════════

    [Fact]
    public void Merge_MultipleBuffers()
    {
        var (coordinator, arena, refCounts) = Create();

        var buf1 = new ThreadLocalBuffer<TestNode>();
        buf1.Append(new TestNode { Value = 1, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });

        var buf2 = new ThreadLocalBuffer<TestNode>();
        buf2.Append(new TestNode { Value = 2, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });

        var handler = new TestHandler(arena);

        coordinator.MergeBuffer(buf1);
        var g1 = coordinator.GetGlobalIndex(0);

        coordinator.MergeBuffer(buf2);
        var g2 = coordinator.GetGlobalIndex(0);

        Assert.NotEqual(g1, g2);
        Assert.Equal(1, arena[g1].Value);
        Assert.Equal(2, arena[g2].Value);

        coordinator.ProcessReleases([buf1, buf2], handler);
    }

    // ═══════════════════════════ Clear and reuse ═════════════════════════

    [Fact]
    public void BufferReuse_AfterClear_WorksCorrectly()
    {
        var (coordinator, arena, refCounts) = Create();

        var buffer = new ThreadLocalBuffer<TestNode>();
        buffer.Append(new TestNode { Value = 1, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });
        coordinator.MergeBuffer(buffer);
        var firstGlobal = coordinator.GetGlobalIndex(0);

        buffer.Clear();
        buffer.Append(new TestNode { Value = 2, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });
        coordinator.MergeBuffer(buffer);
        var secondGlobal = coordinator.GetGlobalIndex(0);

        Assert.NotEqual(firstGlobal, secondGlobal);
        Assert.Equal(1, arena[firstGlobal].Value);
        Assert.Equal(2, arena[secondGlobal].Value);
    }

    // ═══════════════════════════ Reusable map doesn't allocate ═══════════

    [Fact]
    public void Merge_ReusesMaps_DoesNotAllocateInSteadyState()
    {
        var (coordinator, arena, _) = Create(directoryLength: 64, slabShift: 4);
        var handler = new TestHandler(arena);

        var buffer = new ThreadLocalBuffer<TestNode>();

        // Warmup: first merge grows the internal maps.
        for (var i = 0; i < 50; i++)
            buffer.Append(new TestNode { Value = i, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });
        coordinator.MergeBuffer(buffer);
        coordinator.ProcessReleases([buffer], handler);

        var ta = coordinator.GetTestAccessor();
        var mapLengthAfterWarmup = ta.LocalToGlobalMap.Length;

        // Subsequent merges with same or fewer nodes should not grow.
        buffer.Clear();
        for (var i = 0; i < 50; i++)
            buffer.Append(new TestNode { Value = i, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });

        coordinator.MergeBuffer(buffer);
        Assert.Equal(mapLengthAfterWarmup, ta.LocalToGlobalMap.Length);
    }

    // ═══════════════════════════ Full pipeline: path-copy tree ═══════════

    [Fact]
    public void Pipeline_PathCopy_NewVersion_SharedSubtrees()
    {
        var (coordinator, arena, refCounts) = Create(directoryLength: 64, slabShift: 4);

        // Build initial tree: 7 nodes (depth 2 complete binary tree).
        //       0
        //      / \
        //     1   2
        //    / \ / \
        //   3  4 5  6
        var buf0 = new ThreadLocalBuffer<TestNode>();
        var n3 = buf0.Append(new TestNode { Value = 3, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });
        var n4 = buf0.Append(new TestNode { Value = 4, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });
        var n5 = buf0.Append(new TestNode { Value = 5, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });
        var n6 = buf0.Append(new TestNode { Value = 6, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });
        var n1 = buf0.Append(new TestNode { Value = 1, Left = ArenaIndex.TagLocal(n3), Right = ArenaIndex.TagLocal(n4) });
        var n2 = buf0.Append(new TestNode { Value = 2, Left = ArenaIndex.TagLocal(n5), Right = ArenaIndex.TagLocal(n6) });
        var n0 = buf0.Append(new TestNode { Value = 0, Left = ArenaIndex.TagLocal(n1), Right = ArenaIndex.TagLocal(n2) });

        var handler = new TestHandler(arena);
        coordinator.MergeBuffer(buf0);

        var g = new int[7];
        for (var i = 0; i < 7; i++)
            g[i] = coordinator.GetGlobalIndex(i);

        // Make root a live reference (refcount 1).
        refCounts.Increment(g[n0]);

        // Verify tree structure.
        Assert.Equal(g[n1], arena[g[n0]].Left);
        Assert.Equal(g[n2], arena[g[n0]].Right);
        Assert.Equal(g[n3], arena[g[n1]].Left);
        Assert.Equal(g[n4], arena[g[n1]].Right);

        // Verify child refcounts (each child has exactly 1 parent).
        Assert.Equal(1, refCounts.GetCount(g[n0]));
        Assert.Equal(1, refCounts.GetCount(g[n1]));
        Assert.Equal(1, refCounts.GetCount(g[n2]));
        Assert.Equal(1, refCounts.GetCount(g[n3]));
        Assert.Equal(1, refCounts.GetCount(g[n4]));
        Assert.Equal(1, refCounts.GetCount(g[n5]));
        Assert.Equal(1, refCounts.GetCount(g[n6]));

        // Path-copy: change leaf 3 → new leaf 3'. Rebuilds spine: 3'→1'→0'.
        // New tree shares subtrees: node 4 (from old 1), and entire right subtree (node 2).
        var buf1 = new ThreadLocalBuffer<TestNode>();
        var newN3 = buf1.Append(new TestNode { Value = 30, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });
        var newN1 = buf1.Append(new TestNode { Value = 10, Left = ArenaIndex.TagLocal(newN3), Right = g[n4] });
        var newN0 = buf1.Append(new TestNode { Value = 100, Left = ArenaIndex.TagLocal(newN1), Right = g[n2] });

        coordinator.MergeBuffer(buf1);
        buf1.LogRelease(g[n0]);

        var gNew3 = coordinator.GetGlobalIndex(0);
        var gNew1 = coordinator.GetGlobalIndex(1);
        var gNew0 = coordinator.GetGlobalIndex(2);

        // Increment new root.
        refCounts.Increment(gNew0);

        // Process releases (old root).
        coordinator.ProcessReleases([buf1], handler);

        // Shared nodes should have refcount bumped by new tree and decremented by old root release.
        // Node 4: was 1 (from old n1), +1 (from new n1) = 2, then -1 (old n1 freed) = 1.
        Assert.Equal(1, refCounts.GetCount(g[n4]));

        // Node 2: was 1 (from old n0), +1 (from new n0) = 2, then -1 (old n0 freed) = 1.
        Assert.Equal(1, refCounts.GetCount(g[n2]));

        // Old exclusive nodes (n0, n1, n3) should be freed (refcount 0).
        Assert.Equal(0, refCounts.GetCount(g[n0]));
        Assert.Equal(0, refCounts.GetCount(g[n1]));
        Assert.Equal(0, refCounts.GetCount(g[n3]));

        // New tree is intact.
        Assert.Equal(30, arena[gNew3].Value);
        Assert.Equal(10, arena[gNew1].Value);
        Assert.Equal(100, arena[gNew0].Value);
        Assert.Equal(gNew3, arena[gNew1].Left);
        Assert.Equal(g[n4], arena[gNew1].Right);
        Assert.Equal(gNew1, arena[gNew0].Left);
        Assert.Equal(g[n2], arena[gNew0].Right);
    }

    // ═══════════════════════════ Multiple versions then release all ═════

    [Fact]
    public void Pipeline_MultipleVersions_ReleaseAll_EverythingFreed()
    {
        var (coordinator, arena, refCounts) = Create(directoryLength: 64, slabShift: 4);
        var freed = new List<int>();
        var handler = new TestHandler(arena, freed);

        // Build a 3-node chain: leaf -> mid -> root.
        var buf0 = new ThreadLocalBuffer<TestNode>();
        var leaf = buf0.Append(new TestNode { Value = 1, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });
        var mid = buf0.Append(new TestNode { Value = 2, Left = ArenaIndex.TagLocal(leaf), Right = ArenaIndex.NoChild });
        var root = buf0.Append(new TestNode { Value = 3, Left = ArenaIndex.TagLocal(mid), Right = ArenaIndex.NoChild });

        coordinator.MergeBuffer(buf0);
        var gLeaf = coordinator.GetGlobalIndex(0);
        var gMid = coordinator.GetGlobalIndex(1);
        var gRoot = coordinator.GetGlobalIndex(2);
        refCounts.Increment(gRoot);

        // Version 2: replace root -> mid' -> existing leaf.
        var buf1 = new ThreadLocalBuffer<TestNode>();
        var newMid = buf1.Append(new TestNode { Value = 20, Left = gLeaf, Right = ArenaIndex.NoChild });
        var newRoot = buf1.Append(new TestNode { Value = 30, Left = ArenaIndex.TagLocal(newMid), Right = ArenaIndex.NoChild });

        coordinator.MergeBuffer(buf1);
        var gNewMid = coordinator.GetGlobalIndex(0);
        var gNewRoot = coordinator.GetGlobalIndex(1);
        refCounts.Increment(gNewRoot);

        // Release old root.
        buf1.LogRelease(gRoot);
        coordinator.ProcessReleases([buf1], handler);

        // Old root and old mid freed. Leaf still alive (new mid references it).
        Assert.Contains(gRoot, freed);
        Assert.Contains(gMid, freed);
        Assert.DoesNotContain(gLeaf, freed);
        Assert.Equal(1, refCounts.GetCount(gLeaf));

        freed.Clear();

        // Release new root.
        var buf2 = new ThreadLocalBuffer<TestNode>();
        buf2.LogRelease(gNewRoot);
        coordinator.ProcessReleases([buf2], handler);

        Assert.Contains(gNewRoot, freed);
        Assert.Contains(gNewMid, freed);
        Assert.Contains(gLeaf, freed);
    }

    // ═══════════════════════════ Steady-state recycling ═══════════════════

    [Fact]
    public void SteadyState_FreedSlotsAreRecycled()
    {
        var (coordinator, arena, refCounts) = Create(directoryLength: 64, slabShift: 4);
        var handler = new TestHandler(arena);

        // Create initial nodes.
        var buf0 = new ThreadLocalBuffer<TestNode>();
        for (var i = 0; i < 10; i++)
            buf0.Append(new TestNode { Value = i, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });

        coordinator.MergeBuffer(buf0);

        var originalGlobals = new int[10];
        for (var i = 0; i < 10; i++)
        {
            originalGlobals[i] = coordinator.GetGlobalIndex(i);
            refCounts.Increment(originalGlobals[i]);
        }

        // Release all.
        var relBuf = new ThreadLocalBuffer<TestNode>();
        for (var i = 0; i < 10; i++)
            relBuf.LogRelease(originalGlobals[i]);
        coordinator.ProcessReleases([relBuf], handler);

        var highWaterBefore = arena.GetTestAccessor().HighWater;

        // Allocate 10 more — should reuse freed slots.
        var buf1 = new ThreadLocalBuffer<TestNode>();
        for (var i = 0; i < 10; i++)
            buf1.Append(new TestNode { Value = i + 100, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });

        coordinator.MergeBuffer(buf1);

        // High water should not have advanced since all slots were recycled.
        Assert.Equal(highWaterBefore, arena.GetTestAccessor().HighWater);
    }

    // ═══════════════════════════ Mixed local + global children ═══════════

    [Fact]
    public void MergeBuffer_MixedLocalAndGlobalChildren()
    {
        var (coordinator, arena, refCounts) = Create();

        // Pre-populate an existing global node.
        var existingIdx = arena.Allocate();
        arena[existingIdx] = new TestNode { Value = 99, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild };
        refCounts.EnsureCapacity(existingIdx + 1);
        refCounts.Increment(existingIdx);

        // Worker creates: newLeaf (local), newRoot(left=newLeaf tagged local, right=existing global).
        var buffer = new ThreadLocalBuffer<TestNode>();
        var newLeafIdx = buffer.Append(new TestNode { Value = 1, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });
        buffer.Append(new TestNode
        {
            Value = 2,
            Left = ArenaIndex.TagLocal(newLeafIdx),
            Right = existingIdx,
        });

        coordinator.MergeBuffer(buffer);

        var gLeaf = coordinator.GetGlobalIndex(0);
        var gRoot = coordinator.GetGlobalIndex(1);

        // Left should be the remapped local, right should be the preserved global.
        Assert.Equal(gLeaf, arena[gRoot].Left);
        Assert.Equal(existingIdx, arena[gRoot].Right);

        // Both children got their refcounts incremented.
        Assert.Equal(1, refCounts.GetCount(gLeaf));
        Assert.Equal(2, refCounts.GetCount(existingIdx));
    }
}
