using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class RefCountTableTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    private static RefCountTable CreateTinyTable(int directoryLength = 4, int slabShift = 2)
        => new(directoryLength, slabShift);

    /// <summary>Tracks freed indices, no children to decrement.</summary>
    private readonly struct TrackingHandler(List<int> freed) : RefCountTable.IRefCountHandler
    {
        public readonly void OnFreed(int index, RefCountTable table)
            => freed.Add(index);
    }

    /// <summary>No-op: ignores frees, no children.</summary>
    private readonly struct NullHandler : RefCountTable.IRefCountHandler
    {
        public readonly void OnFreed(int index, RefCountTable table) { }
    }

    /// <summary>Binary tree: children of index i are 2i+1 and 2i+2. Tracks frees.</summary>
    private readonly struct BinaryTreeHandler(int maxIndex, List<int> freed) : RefCountTable.IRefCountHandler
    {
        public readonly void OnFreed(int index, RefCountTable table)
        {
            freed.Add(index);
            var left = (index * 2) + 1;
            var right = (index * 2) + 2;
            if (left <= maxIndex)
                table.Decrement(left, this);
            if (right <= maxIndex)
                table.Decrement(right, this);
        }
    }

    /// <summary>Linear chain: each node points to index+1. Tracks frees.</summary>
    private readonly struct LinearChainHandler(int maxIndex, List<int> freed) : RefCountTable.IRefCountHandler
    {
        public readonly void OnFreed(int index, RefCountTable table)
        {
            freed.Add(index);
            var next = index + 1;
            if (next <= maxIndex)
                table.Decrement(next, this);
        }
    }

    /// <summary>Wide tree: node 0 points to 1..fanout. Tracks frees.</summary>
    private readonly struct WideTreeHandler(int fanout, List<int> freed) : RefCountTable.IRefCountHandler
    {
        public readonly void OnFreed(int index, RefCountTable table)
        {
            freed.Add(index);
            if (index != 0)
                return;
            for (var i = 1; i <= fanout; i++)
                table.Decrement(i, this);
        }
    }

    /// <summary>Single parent→child relationship. Tracks frees.</summary>
    private readonly struct SingleChildHandler(int parentIndex, int childIndex, List<int> freed) : RefCountTable.IRefCountHandler
    {
        public readonly void OnFreed(int index, RefCountTable table)
        {
            freed.Add(index);
            if (index == parentIndex)
                table.Decrement(childIndex, this);
        }
    }

    // ═══════════════════════════ EnsureCapacity ═══════════════════════════

    [Fact]
    public void EnsureCapacity_AllocatesRequiredSlabs()
    {
        var table = CreateTinyTable(directoryLength: 4, slabShift: 2);
        var ta = table.GetTestAccessor();

        for (var i = 0; i < 4; i++)
            Assert.Null(ta.Directory[i]);

        table.EnsureCapacity(5);
        Assert.NotNull(ta.Directory[0]);
        Assert.NotNull(ta.Directory[1]);
        Assert.Null(ta.Directory[2]);
    }

    [Fact]
    public void EnsureCapacity_MultipleSlabs()
    {
        var table = CreateTinyTable(directoryLength: 8, slabShift: 2);
        var ta = table.GetTestAccessor();

        table.EnsureCapacity(12);
        Assert.NotNull(ta.Directory[0]);
        Assert.NotNull(ta.Directory[1]);
        Assert.NotNull(ta.Directory[2]);
        Assert.Null(ta.Directory[3]);
    }

    [Fact]
    public void EnsureCapacity_Zero_DoesNothing()
    {
        var table = CreateTinyTable(directoryLength: 4, slabShift: 2);
        var ta = table.GetTestAccessor();

        table.EnsureCapacity(0);
        for (var i = 0; i < 4; i++)
            Assert.Null(ta.Directory[i]);
    }

    [Fact]
    public void EnsureCapacity_Idempotent()
    {
        var table = CreateTinyTable(directoryLength: 4, slabShift: 2);
        var ta = table.GetTestAccessor();

        table.EnsureCapacity(4);
        var slab0 = ta.Directory[0];

        table.EnsureCapacity(4);
        Assert.Same(slab0, ta.Directory[0]);
    }

    // ═══════════════════════════ Basic increment/decrement ════════════════

    [Fact]
    public void Increment_SetsCountToOne()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(1);
        table.Increment(0);
        Assert.Equal(1, table.GetCount(0));
    }

    [Fact]
    public void Increment_Twice_SetsCountToTwo()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(1);
        table.Increment(0);
        table.Increment(0);
        Assert.Equal(2, table.GetCount(0));
    }

    [Fact]
    public void Decrement_FromTwo_SetsCountToOne_NoFree()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(1);
        var freed = new List<int>();
        table.Increment(0);
        table.Increment(0);
        table.Decrement(0, new TrackingHandler(freed));
        Assert.Equal(1, table.GetCount(0));
        Assert.Empty(freed);
    }

    [Fact]
    public void Decrement_ToZero_FiresOnFreed()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(1);
        var freed = new List<int>();
        table.Increment(0);
        table.Decrement(0, new TrackingHandler(freed));
        Assert.Equal(0, table.GetCount(0));
        Assert.Single(freed);
        Assert.Equal(0, freed[0]);
    }

    [Fact]
    public void GetCount_Uninitialized_ReturnsZero()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(2);
        table.Increment(0);
        Assert.Equal(0, table.GetCount(1));
    }

    [Fact]
    public void MultipleIndices_Independent()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(3);
        table.Increment(0);
        table.Increment(0);
        table.Increment(1);
        table.Increment(2);
        table.Increment(2);
        table.Increment(2);

        Assert.Equal(2, table.GetCount(0));
        Assert.Equal(1, table.GetCount(1));
        Assert.Equal(3, table.GetCount(2));
    }

    // ═══════════════════════════ Cascade-free: binary tree ════════════════

    [Fact]
    public void CascadeDecrement_SingleNode_FreesJustThatNode()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(1);
        var freed = new List<int>();
        table.Increment(0);

        table.Decrement(0, new BinaryTreeHandler(0, freed));

        Assert.Equal(0, table.GetCount(0));
        Assert.Single(freed);
        Assert.Equal(0, freed[0]);
    }

    [Fact]
    public void CascadeDecrement_SmallBinaryTree()
    {
        var table = CreateTinyTable(directoryLength: 4, slabShift: 2);
        table.EnsureCapacity(7);
        var freed = new List<int>();

        for (var i = 0; i <= 6; i++)
            table.Increment(i);

        table.Decrement(0, new BinaryTreeHandler(6, freed));

        for (var i = 0; i <= 6; i++)
            Assert.Equal(0, table.GetCount(i));

        Assert.Equal(7, freed.Count);
        Assert.Contains(0, freed);
        Assert.Contains(6, freed);
    }

    [Fact]
    public void CascadeDecrement_SharedChild_NotFreedUntilBothParentsRelease()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(3);
        var freed = new List<int>();

        table.Increment(0);
        table.Increment(1);
        table.Increment(1); // shared: two parents
        table.Increment(2);

        table.Decrement(0, new SingleChildHandler(0, 1, freed));

        Assert.Equal(1, table.GetCount(1));
        Assert.Single(freed);
        Assert.Equal(0, freed[0]);

        freed.Clear();
        table.Decrement(2, new SingleChildHandler(2, 1, freed));

        Assert.Equal(0, table.GetCount(1));
        Assert.Equal(2, freed.Count);
        Assert.Contains(2, freed);
        Assert.Contains(1, freed);
    }

    [Fact]
    public void CascadeDecrement_DoesNotFreeWhenRefcountStaysAboveZero()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(1);
        var freed = new List<int>();

        table.Increment(0);
        table.Increment(0);

        table.Decrement(0, new BinaryTreeHandler(0, freed));

        Assert.Equal(1, table.GetCount(0));
        Assert.Empty(freed);
    }

    // ═══════════════════════════ Cascade-free: linear chain ═══════════════

    [Fact]
    public void CascadeDecrement_LinearChain_FreesAll()
    {
        const int ChainLength = 100;
        var table = new RefCountTable(directoryLength: 4, slabShift: 5);
        table.EnsureCapacity(ChainLength);
        var freed = new List<int>();

        for (var i = 0; i < ChainLength; i++)
            table.Increment(i);

        table.Decrement(0, new LinearChainHandler(ChainLength - 1, freed));

        Assert.Equal(ChainLength, freed.Count);
        for (var i = 0; i < ChainLength; i++)
            Assert.Equal(0, table.GetCount(i));
    }

    [Fact]
    public void CascadeDecrement_DeepChain_NoStackOverflow()
    {
        const int Depth = 10_000;
        var table = new RefCountTable();
        table.EnsureCapacity(Depth);
        var freed = new List<int>();

        for (var i = 0; i < Depth; i++)
            table.Increment(i);

        table.Decrement(0, new LinearChainHandler(Depth - 1, freed));

        Assert.Equal(Depth, freed.Count);
    }

    // ═══════════════════════════ Cascade-free: wide tree ══════════════════

    [Fact]
    public void CascadeDecrement_WideTree_FreesAll()
    {
        const int Fanout = 50;
        var table = CreateTinyTable(directoryLength: 8, slabShift: 3);
        table.EnsureCapacity(Fanout + 1);
        var freed = new List<int>();

        table.Increment(0);
        for (var i = 1; i <= Fanout; i++)
            table.Increment(i);

        table.Decrement(0, new WideTreeHandler(Fanout, freed));

        Assert.Equal(Fanout + 1, freed.Count);
        Assert.Contains(0, freed);
        for (var i = 1; i <= Fanout; i++)
        {
            Assert.Equal(0, table.GetCount(i));
            Assert.Contains(i, freed);
        }
    }

    // ═══════════════════════════ Bulk operations ══════════════════════════

    [Fact]
    public void IncrementBatch_IncrementsAll()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(3);
        int[] indices = [0, 1, 2, 0, 1, 0];
        table.IncrementBatch(indices);

        Assert.Equal(3, table.GetCount(0));
        Assert.Equal(2, table.GetCount(1));
        Assert.Equal(1, table.GetCount(2));
    }

    [Fact]
    public void DecrementBatch_DecrementsAll_FreesAtZero_WithCascade()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(3);
        var freed = new List<int>();

        table.Increment(0);
        table.Increment(0);
        table.Increment(1);
        table.Increment(2);

        int[] batch = [0, 1, 2];
        table.DecrementBatch(batch, new TrackingHandler(freed));

        Assert.Equal(1, table.GetCount(0));
        Assert.Equal(0, table.GetCount(1));
        Assert.Equal(0, table.GetCount(2));

        Assert.Equal(2, freed.Count);
        Assert.Contains(1, freed);
        Assert.Contains(2, freed);
    }

    [Fact]
    public void DecrementBatch_CascadesThroughChildren()
    {
        var table = CreateTinyTable(directoryLength: 4, slabShift: 2);
        table.EnsureCapacity(7);
        var freed = new List<int>();

        for (var i = 0; i <= 6; i++)
            table.Increment(i);

        int[] batch = [0];
        table.DecrementBatch(batch, new BinaryTreeHandler(6, freed));

        Assert.Equal(7, freed.Count);
        for (var i = 0; i <= 6; i++)
            Assert.Equal(0, table.GetCount(i));
    }

    [Fact]
    public void DecrementBatch_Empty_DoesNothing()
    {
        var table = CreateTinyTable();
        var freed = new List<int>();
        table.DecrementBatch([], new TrackingHandler(freed));
        Assert.Empty(freed);
    }

    // ═══════════════════════════ Slab boundaries ═════════════════════════

    [Fact]
    public void Increment_CrossesSlabBoundary()
    {
        var table = CreateTinyTable(directoryLength: 4, slabShift: 2);
        table.EnsureCapacity(5);
        var ta = table.GetTestAccessor();

        table.Increment(3);
        table.Increment(4);

        Assert.NotNull(ta.Directory[0]);
        Assert.NotNull(ta.Directory[1]);
        Assert.Equal(1, table.GetCount(3));
        Assert.Equal(1, table.GetCount(4));
    }

    // ═══════════════════════════ Default constructor ══════════════════════

    [Fact]
    public void DefaultConstructor_UsesExpectedParameters()
    {
        var table = new RefCountTable();
        var ta = table.GetTestAccessor();

        Assert.Equal(65_536, ta.DirectoryLength);
        Assert.Equal(SlabSizeHelper<int>.SlabLength, ta.SlabLength);
        Assert.Equal(SlabSizeHelper<int>.SlabShift, ta.SlabShift);
    }

    // ═══════════════════════════ Interleaved patterns ═════════════════════

    [Fact]
    public void InterleavedIncrementDecrement_MaintainsCorrectCounts()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(2);
        var freed = new List<int>();

        table.Increment(0);
        table.Increment(0);
        table.Increment(1);

        table.Decrement(0, new NullHandler());
        Assert.Equal(1, table.GetCount(0));

        table.Increment(0);
        Assert.Equal(2, table.GetCount(0));

        table.Decrement(0, new TrackingHandler(freed));
        table.Decrement(0, new TrackingHandler(freed));
        Assert.Equal(0, table.GetCount(0));
        Assert.Single(freed);
    }

    [Fact]
    public void ManyIncrementsThenBatchDecrement()
    {
        var table = CreateTinyTable(directoryLength: 8, slabShift: 3);
        const int Count = 20;
        table.EnsureCapacity(Count);
        var freed = new List<int>();

        var indices = new int[Count];
        for (var i = 0; i < Count; i++)
        {
            indices[i] = i;
            table.Increment(i);
        }

        table.DecrementBatch(indices, new TrackingHandler(freed));

        Assert.Equal(Count, freed.Count);
        for (var i = 0; i < Count; i++)
            Assert.Equal(0, table.GetCount(i));
    }

    // ═══════════════════════════ Two-table cross-cascade ═════════════════

    private sealed class TwoTableContext
    {
        public RefCountTable TableA { get; set; } = null!;
        public RefCountTable TableB { get; set; } = null!;
        public List<int> FreedA { get; } = [];
        public List<int> FreedB { get; } = [];
    }

    private readonly struct TwoTableAHandler(TwoTableContext ctx) : RefCountTable.IRefCountHandler
    {
        public readonly void OnFreed(int index, RefCountTable table)
        {
            ctx.FreedA.Add(index);
            if (index == 0)
                ctx.TableB.Decrement(0, new TwoTableBHandler(ctx));
        }
    }

    private readonly struct TwoTableBHandler(TwoTableContext ctx) : RefCountTable.IRefCountHandler
    {
        public readonly void OnFreed(int index, RefCountTable table)
        {
            ctx.FreedB.Add(index);
            if (index == 0)
                ctx.TableA.Decrement(1, new TwoTableAHandler(ctx));
        }
    }

    [Fact]
    public void TwoTable_BounceBack_CascadesCorrectly()
    {
        var ctx = new TwoTableContext
        {
            TableA = CreateTinyTable(directoryLength: 4, slabShift: 2),
            TableB = CreateTinyTable(directoryLength: 4, slabShift: 2),
        };
        ctx.TableA.EnsureCapacity(2);
        ctx.TableB.EnsureCapacity(1);

        ctx.TableA.Increment(0);
        ctx.TableA.Increment(1);
        ctx.TableB.Increment(0);

        ctx.TableA.Decrement(0, new TwoTableAHandler(ctx));

        Assert.Equal(0, ctx.TableA.GetCount(0));
        Assert.Equal(0, ctx.TableA.GetCount(1));
        Assert.Equal(0, ctx.TableB.GetCount(0));

        Assert.Equal(2, ctx.FreedA.Count);
        Assert.Contains(0, ctx.FreedA);
        Assert.Contains(1, ctx.FreedA);
        Assert.Single(ctx.FreedB);
        Assert.Equal(0, ctx.FreedB[0]);
    }

    [Fact]
    public void TwoTable_BounceBack_SharedChildSurvives()
    {
        var ctx = new TwoTableContext
        {
            TableA = CreateTinyTable(directoryLength: 4, slabShift: 2),
            TableB = CreateTinyTable(directoryLength: 4, slabShift: 2),
        };
        ctx.TableA.EnsureCapacity(2);
        ctx.TableB.EnsureCapacity(1);

        ctx.TableA.Increment(0);
        ctx.TableA.Increment(1);
        ctx.TableA.Increment(1); // A[1] = 2 (shared)
        ctx.TableB.Increment(0);

        ctx.TableA.Decrement(0, new TwoTableAHandler(ctx));

        Assert.Equal(0, ctx.TableA.GetCount(0));
        Assert.Equal(1, ctx.TableA.GetCount(1)); // survived
        Assert.Equal(0, ctx.TableB.GetCount(0));

        Assert.Single(ctx.FreedA);
        Assert.Equal(0, ctx.FreedA[0]);
        Assert.Single(ctx.FreedB);
        Assert.Equal(0, ctx.FreedB[0]);
    }

    // ═══════════════════════════ Three-table cross-cascade ═══════════════

    // Data-driven infrastructure for cross-table cascade tests with three tables (A=0, B=1, C=2).
    // Nodes are identified by (tableId, index). Edges define parent→child relationships across tables.
    // The MultiTableHandler routes OnFreed callbacks and child decrements through the topology map.

    private sealed class MultiTableContext
    {
        public const int A = 0, B = 1, C = 2;

        public RefCountTable[] Tables { get; } = new RefCountTable[3];
        public List<int>[] Freed { get; } = [[], [], []];
        private readonly Dictionary<int, List<(int tableId, int index)>>[] _edges = [[], [], []];

        public MultiTableContext(int nodesPerTable = 8)
        {
            for (var t = 0; t < 3; t++)
            {
                this.Tables[t] = CreateTinyTable(directoryLength: 4, slabShift: 3);
                this.Tables[t].EnsureCapacity(nodesPerTable);
            }
        }

        public void AddEdge(int parentTable, int parentIndex, int childTable, int childIndex)
        {
            if (!_edges[parentTable].TryGetValue(parentIndex, out var list))
            {
                list = [];
                _edges[parentTable][parentIndex] = list;
            }

            list.Add((childTable, childIndex));
        }

        public List<(int tableId, int index)>? GetChildren(int tableId, int index)
            => _edges[tableId].GetValueOrDefault(index);

        public void Decrement(int tableId, int index)
            => this.Tables[tableId].Decrement(index, new MultiTableHandler(this, tableId));
    }

    private readonly struct MultiTableHandler(MultiTableContext ctx, int tableId) : RefCountTable.IRefCountHandler
    {
        public readonly void OnFreed(int index, RefCountTable table)
        {
            ctx.Freed[tableId].Add(index);
            var children = ctx.GetChildren(tableId, index);
            if (children == null) return;
            foreach (var (childTableId, childIndex) in children)
                ctx.Tables[childTableId].Decrement(childIndex, new MultiTableHandler(ctx, childTableId));
        }
    }

    // ── Data providers ───────────────────────────────────────────────────

    /// <summary>All 21 (origin, childSubset) combinations: 3 origins × 7 non-empty subsets of {A,B,C}.</summary>
    public static IEnumerable<object[]> AllChildSubsets()
    {
        int[][] subsets = [[0], [1], [2], [0, 1], [0, 2], [1, 2], [0, 1, 2]];
        for (var origin = 0; origin < 3; origin++)
            foreach (var subset in subsets)
                yield return [origin, subset];
    }

    /// <summary>All 6 orderings of a three-table chain: X[0]→Y[0]→Z[0].</summary>
    public static IEnumerable<object[]> AllThreeTableChainOrders()
    {
        yield return [0, 1, 2];
        yield return [0, 2, 1];
        yield return [1, 0, 2];
        yield return [1, 2, 0];
        yield return [2, 0, 1];
        yield return [2, 1, 0];
    }

    // ── Single-hop: every child-set permutation from every origin ────────

    [Theory]
    [MemberData(nameof(AllChildSubsets))]
    public void ThreeTable_SingleHop_AllChildSubsets(int origin, int[] childTables)
    {
        var ctx = new MultiTableContext();
        ctx.Tables[origin].Increment(0);

        foreach (var ct in childTables)
        {
            ctx.Tables[ct].Increment(1);
            ctx.AddEdge(origin, 0, ct, 1);
        }

        ctx.Decrement(origin, 0);

        Assert.Equal(0, ctx.Tables[origin].GetCount(0));
        Assert.Contains(0, ctx.Freed[origin]);

        foreach (var ct in childTables)
        {
            Assert.Equal(0, ctx.Tables[ct].GetCount(1));
            Assert.Contains(1, ctx.Freed[ct]);
        }

        for (var t = 0; t < 3; t++)
        {
            if (t == origin || childTables.Contains(t))
                continue;
            Assert.Empty(ctx.Freed[t]);
        }
    }

    [Theory]
    [MemberData(nameof(AllChildSubsets))]
    public void ThreeTable_SingleHop_SharedChildren_Survive(int origin, int[] childTables)
    {
        var ctx = new MultiTableContext();
        ctx.Tables[origin].Increment(0);

        foreach (var ct in childTables)
        {
            ctx.Tables[ct].Increment(1);
            ctx.Tables[ct].Increment(1); // rc=2: shared by another parent
            ctx.AddEdge(origin, 0, ct, 1);
        }

        // Phase 1: cascade from root — children survive with rc=1
        ctx.Decrement(origin, 0);

        Assert.Equal(0, ctx.Tables[origin].GetCount(0));
        Assert.Contains(0, ctx.Freed[origin]);

        foreach (var ct in childTables)
        {
            Assert.Equal(1, ctx.Tables[ct].GetCount(1));
            Assert.DoesNotContain(1, ctx.Freed[ct]);
        }

        // Phase 2: explicitly free each child
        foreach (var ct in childTables)
            ctx.Decrement(ct, 1);

        foreach (var ct in childTables)
        {
            Assert.Equal(0, ctx.Tables[ct].GetCount(1));
            Assert.Contains(1, ctx.Freed[ct]);
        }
    }

    // ── Chain through all three tables ───────────────────────────────────

    [Theory]
    [MemberData(nameof(AllThreeTableChainOrders))]
    public void ThreeTable_Chain_AllOrders(int first, int second, int third)
    {
        var ctx = new MultiTableContext();

        ctx.Tables[first].Increment(0);
        ctx.Tables[second].Increment(0);
        ctx.Tables[third].Increment(0);

        ctx.AddEdge(first, 0, second, 0);
        ctx.AddEdge(second, 0, third, 0);

        ctx.Decrement(first, 0);

        Assert.Equal(0, ctx.Tables[first].GetCount(0));
        Assert.Equal(0, ctx.Tables[second].GetCount(0));
        Assert.Equal(0, ctx.Tables[third].GetCount(0));

        Assert.Contains(0, ctx.Freed[first]);
        Assert.Contains(0, ctx.Freed[second]);
        Assert.Contains(0, ctx.Freed[third]);
    }

    // ── Named topology tests ─────────────────────────────────────────────

    [Fact]
    public void ThreeTable_Chain_LoopBackToOriginTable()
    {
        const int A = MultiTableContext.A, B = MultiTableContext.B, C = MultiTableContext.C;
        var ctx = new MultiTableContext();

        // A[0] → B[0] → C[0] → A[1]
        ctx.Tables[A].Increment(0);
        ctx.Tables[B].Increment(0);
        ctx.Tables[C].Increment(0);
        ctx.Tables[A].Increment(1);

        ctx.AddEdge(A, 0, B, 0);
        ctx.AddEdge(B, 0, C, 0);
        ctx.AddEdge(C, 0, A, 1);

        ctx.Decrement(A, 0);

        Assert.Equal(0, ctx.Tables[A].GetCount(0));
        Assert.Equal(0, ctx.Tables[A].GetCount(1));
        Assert.Equal(0, ctx.Tables[B].GetCount(0));
        Assert.Equal(0, ctx.Tables[C].GetCount(0));

        Assert.Equal(2, ctx.Freed[A].Count);
        Assert.Contains(0, ctx.Freed[A]);
        Assert.Contains(1, ctx.Freed[A]);
        Assert.Single(ctx.Freed[B]);
        Assert.Single(ctx.Freed[C]);
    }

    [Fact]
    public void ThreeTable_Diamond_SharedGrandchild()
    {
        const int A = MultiTableContext.A, B = MultiTableContext.B, C = MultiTableContext.C;
        var ctx = new MultiTableContext();

        // A[0] → {B[0], C[0]}, both B[0] and C[0] → A[1]. A[1] rc=2 (two paths converge).
        ctx.Tables[A].Increment(0);
        ctx.Tables[B].Increment(0);
        ctx.Tables[C].Increment(0);
        ctx.Tables[A].Increment(1);
        ctx.Tables[A].Increment(1); // rc=2

        ctx.AddEdge(A, 0, B, 0);
        ctx.AddEdge(A, 0, C, 0);
        ctx.AddEdge(B, 0, A, 1);
        ctx.AddEdge(C, 0, A, 1);

        ctx.Decrement(A, 0);

        Assert.Equal(0, ctx.Tables[A].GetCount(0));
        Assert.Equal(0, ctx.Tables[A].GetCount(1));
        Assert.Equal(0, ctx.Tables[B].GetCount(0));
        Assert.Equal(0, ctx.Tables[C].GetCount(0));

        Assert.Equal(2, ctx.Freed[A].Count);
        Assert.Single(ctx.Freed[B]);
        Assert.Single(ctx.Freed[C]);
    }

    [Fact]
    public void ThreeTable_FanIn_SharedChild_PartialThenFull()
    {
        const int A = MultiTableContext.A, B = MultiTableContext.B, C = MultiTableContext.C;
        var ctx = new MultiTableContext();

        // A[0] → C[0], B[0] → C[0]. C[0] rc=2.
        ctx.Tables[A].Increment(0);
        ctx.Tables[B].Increment(0);
        ctx.Tables[C].Increment(0);
        ctx.Tables[C].Increment(0); // rc=2

        ctx.AddEdge(A, 0, C, 0);
        ctx.AddEdge(B, 0, C, 0);

        // First root: C[0] survives
        ctx.Decrement(A, 0);
        Assert.Equal(1, ctx.Tables[C].GetCount(0));
        Assert.Empty(ctx.Freed[C]);

        // Second root: C[0] now frees
        ctx.Decrement(B, 0);
        Assert.Equal(0, ctx.Tables[C].GetCount(0));
        Assert.Single(ctx.Freed[C]);
    }

    [Fact]
    public void ThreeTable_ComplexGraph_AllTablesInterconnected()
    {
        const int A = MultiTableContext.A, B = MultiTableContext.B, C = MultiTableContext.C;
        var ctx = new MultiTableContext();

        // A[0] → {B[0], C[0]}, B[0] → {A[1], C[1]}, C[0] → {A[2]}, C[1] → {B[1]}
        ctx.Tables[A].Increment(0);
        ctx.Tables[A].Increment(1);
        ctx.Tables[A].Increment(2);
        ctx.Tables[B].Increment(0);
        ctx.Tables[B].Increment(1);
        ctx.Tables[C].Increment(0);
        ctx.Tables[C].Increment(1);

        ctx.AddEdge(A, 0, B, 0);
        ctx.AddEdge(A, 0, C, 0);
        ctx.AddEdge(B, 0, A, 1);
        ctx.AddEdge(B, 0, C, 1);
        ctx.AddEdge(C, 0, A, 2);
        ctx.AddEdge(C, 1, B, 1);

        ctx.Decrement(A, 0);

        for (var i = 0; i < 3; i++)
            Assert.Equal(0, ctx.Tables[A].GetCount(i));
        for (var i = 0; i < 2; i++)
            Assert.Equal(0, ctx.Tables[B].GetCount(i));
        for (var i = 0; i < 2; i++)
            Assert.Equal(0, ctx.Tables[C].GetCount(i));

        var totalFreed = ctx.Freed[A].Count + ctx.Freed[B].Count + ctx.Freed[C].Count;
        Assert.Equal(7, totalFreed);
        Assert.Equal(3, ctx.Freed[A].Count);
        Assert.Equal(2, ctx.Freed[B].Count);
        Assert.Equal(2, ctx.Freed[C].Count);
    }

    [Fact]
    public void ThreeTable_MultipleRoots_ConvergingPaths()
    {
        const int A = MultiTableContext.A, B = MultiTableContext.B, C = MultiTableContext.C;
        var ctx = new MultiTableContext();

        // Two paths converge: A[0]→B[0]→C[0], A[1]→B[1]→C[0]. C[0] rc=2.
        ctx.Tables[A].Increment(0);
        ctx.Tables[A].Increment(1);
        ctx.Tables[B].Increment(0);
        ctx.Tables[B].Increment(1);
        ctx.Tables[C].Increment(0);
        ctx.Tables[C].Increment(0); // rc=2

        ctx.AddEdge(A, 0, B, 0);
        ctx.AddEdge(A, 1, B, 1);
        ctx.AddEdge(B, 0, C, 0);
        ctx.AddEdge(B, 1, C, 0);

        // First root: C[0] survives
        ctx.Decrement(A, 0);
        Assert.Equal(1, ctx.Tables[C].GetCount(0));
        Assert.Empty(ctx.Freed[C]);

        // Second root: C[0] now frees
        ctx.Decrement(A, 1);
        Assert.Equal(0, ctx.Tables[C].GetCount(0));
        Assert.Single(ctx.Freed[C]);
    }

    [Fact]
    public void ThreeTable_MultipleChildrenInSameTable()
    {
        const int A = MultiTableContext.A, B = MultiTableContext.B;
        var ctx = new MultiTableContext();

        // A[0] → {B[0], B[1], B[2]}: three children in same target table
        ctx.Tables[A].Increment(0);
        ctx.Tables[B].Increment(0);
        ctx.Tables[B].Increment(1);
        ctx.Tables[B].Increment(2);

        ctx.AddEdge(A, 0, B, 0);
        ctx.AddEdge(A, 0, B, 1);
        ctx.AddEdge(A, 0, B, 2);

        ctx.Decrement(A, 0);

        Assert.Equal(0, ctx.Tables[A].GetCount(0));
        Assert.Equal(0, ctx.Tables[B].GetCount(0));
        Assert.Equal(0, ctx.Tables[B].GetCount(1));
        Assert.Equal(0, ctx.Tables[B].GetCount(2));

        Assert.Single(ctx.Freed[A]);
        Assert.Equal(3, ctx.Freed[B].Count);
    }

    [Fact]
    public void ThreeTable_DeepAlternatingChain()
    {
        const int Depth = 30; // 10 nodes per table
        var ctx = new MultiTableContext(nodesPerTable: 12);
        int[] tableOrder = [MultiTableContext.A, MultiTableContext.B, MultiTableContext.C];

        for (var i = 0; i < Depth; i++)
            ctx.Tables[tableOrder[i % 3]].Increment(i / 3);

        for (var i = 0; i < Depth - 1; i++)
            ctx.AddEdge(tableOrder[i % 3], i / 3, tableOrder[(i + 1) % 3], (i + 1) / 3);

        ctx.Decrement(MultiTableContext.A, 0);

        for (var i = 0; i < Depth; i++)
            Assert.Equal(0, ctx.Tables[tableOrder[i % 3]].GetCount(i / 3));

        var totalFreed = ctx.Freed[0].Count + ctx.Freed[1].Count + ctx.Freed[2].Count;
        Assert.Equal(Depth, totalFreed);
        Assert.Equal(10, ctx.Freed[0].Count);
        Assert.Equal(10, ctx.Freed[1].Count);
        Assert.Equal(10, ctx.Freed[2].Count);
    }

    [Fact]
    public void ThreeTable_CascadeActive_FalseOnAllTables_AfterComplexCascade()
    {
        const int A = MultiTableContext.A, B = MultiTableContext.B, C = MultiTableContext.C;
        var ctx = new MultiTableContext();

        // A[0] → B[0] → C[0] → A[1]: loop through all three tables
        ctx.Tables[A].Increment(0);
        ctx.Tables[A].Increment(1);
        ctx.Tables[B].Increment(0);
        ctx.Tables[C].Increment(0);

        ctx.AddEdge(A, 0, B, 0);
        ctx.AddEdge(B, 0, C, 0);
        ctx.AddEdge(C, 0, A, 1);

        ctx.Decrement(A, 0);

        Assert.False(ctx.Tables[A].GetTestAccessor().CascadeActive);
        Assert.False(ctx.Tables[B].GetTestAccessor().CascadeActive);
        Assert.False(ctx.Tables[C].GetTestAccessor().CascadeActive);
    }

    // ═══════════════════════════ Cascade state ════════════════════════════

    [Fact]
    public void CascadeActive_FalseBeforeAndAfterDecrement()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(3);
        var ta = table.GetTestAccessor();

        table.Increment(0);
        table.Increment(1);
        table.Increment(2);

        Assert.False(ta.CascadeActive);
        table.Decrement(0, new LinearChainHandler(2, []));
        Assert.False(ta.CascadeActive);
    }

    // ═══════════════════════════ Debug assertions ═════════════════════════

#if DEBUG
    [Fact]
    public void Debug_MutatingFromDifferentThread_TriggersAssert()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(2);
        table.Increment(0);

        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var listener = new AssertThrowsListener();
                table.Increment(1);
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.Start();
        thread.Join();

        Assert.NotNull(caught);
        Assert.Contains("owner is thread", caught.Message);
    }

    [Fact]
    public void Debug_DecrementBelowZero_TriggersAssert()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(1);
        table.Increment(0);

        Exception? caught = null;
        try
        {
            using var listener = new AssertThrowsListener();
            table.Decrement(0, default(NullHandler));
            table.Decrement(0, default(NullHandler));
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        Assert.Contains("refcount already at", caught.Message);
    }

    private sealed class AssertThrowsListener : System.Diagnostics.TraceListener, IDisposable
    {
        public AssertThrowsListener()
        {
            System.Diagnostics.Trace.Listeners.Clear();
            System.Diagnostics.Trace.Listeners.Add(this);
        }

        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }

        public override void Fail(string? message, string? detailMessage)
            => throw new InvalidOperationException(message ?? detailMessage ?? "Debug.Assert failed");

        void IDisposable.Dispose()
        {
            System.Diagnostics.Trace.Listeners.Remove(this);
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.DefaultTraceListener());
        }
    }
#endif
}
