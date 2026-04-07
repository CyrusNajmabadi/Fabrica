using Fabrica.Core.Collections.Unsafe;
using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class RefCountTableTests
{
    private struct DummyNode;

    // ── Helpers ───────────────────────────────────────────────────────────

    private static RefCountTable<DummyNode> CreateTinyTable(int directoryLength = 4, int slabShift = 2)
        => RefCountTable<DummyNode>.TestAccessor.Create(directoryLength, slabShift);

    /// <summary>
    /// Test-only helper that wraps a <see cref="RefCountTable{T}"/> with cascade behavior.
    /// When a decrement reaches zero, the cascade loop calls the provided callback which may
    /// trigger further decrements (re-entrant pushes onto the pending stack).
    /// </summary>
    private sealed class CascadeRunner(RefCountTable<DummyNode> table)
    {
        private readonly Stack<Handle<DummyNode>> _pending = new();
        private bool _active;
        private Action<Handle<DummyNode>>? _onFreed;

        public void Decrement(Handle<DummyNode> handle, Action<Handle<DummyNode>> onFreed)
        {
            if (!table.Decrement(handle))
                return;
            _pending.Push(handle);
            if (_active) return;
            _active = true;
            _onFreed = onFreed;
            while (_pending.Count > 0)
                _onFreed(_pending.Pop());
            _active = false;
        }

        public void DecrementBatch(ReadOnlySpan<Handle<DummyNode>> handles, Action<Handle<DummyNode>> onFreed)
        {
            for (var i = 0; i < handles.Length; i++)
            {
                if (table.Decrement(handles[i]))
                    _pending.Push(handles[i]);
            }

            if (_pending.Count == 0) return;
            _active = true;
            _onFreed = onFreed;
            while (_pending.Count > 0)
                _onFreed(_pending.Pop());
            _active = false;
        }
    }

    private static Action<Handle<DummyNode>> BinaryTreeCallback(CascadeRunner runner, int maxIndex, List<int> freed)
    {
        void OnFreed(Handle<DummyNode> handle)
        {
            freed.Add(handle.Index);
            var left = (handle.Index * 2) + 1;
            var right = (handle.Index * 2) + 2;
            if (left <= maxIndex) runner.Decrement(new Handle<DummyNode>(left), OnFreed);
            if (right <= maxIndex) runner.Decrement(new Handle<DummyNode>(right), OnFreed);
        }
        return OnFreed;
    }

    private static Action<Handle<DummyNode>> LinearChainCallback(CascadeRunner runner, int maxIndex, List<int> freed)
    {
        void OnFreed(Handle<DummyNode> handle)
        {
            freed.Add(handle.Index);
            var next = handle.Index + 1;
            if (next <= maxIndex) runner.Decrement(new Handle<DummyNode>(next), OnFreed);
        }
        return OnFreed;
    }

    private static Action<Handle<DummyNode>> WideTreeCallback(CascadeRunner runner, int fanout, List<int> freed)
    {
        void OnFreed(Handle<DummyNode> handle)
        {
            freed.Add(handle.Index);
            if (handle.Index != 0) return;
            for (var i = 1; i <= fanout; i++)
                runner.Decrement(new Handle<DummyNode>(i), OnFreed);
        }
        return OnFreed;
    }

    private static Action<Handle<DummyNode>> SingleChildCallback(CascadeRunner runner, int parentIndex, int childIndex, List<int> freed)
    {
        void OnFreed(Handle<DummyNode> handle)
        {
            freed.Add(handle.Index);
            if (handle.Index == parentIndex)
                runner.Decrement(new Handle<DummyNode>(childIndex), OnFreed);
        }
        return OnFreed;
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
        table.Increment(new Handle<DummyNode>(0));
        Assert.Equal(1, table.GetCount(new Handle<DummyNode>(0)));
    }

    [Fact]
    public void Increment_Twice_SetsCountToTwo()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(1);
        table.Increment(new Handle<DummyNode>(0));
        table.Increment(new Handle<DummyNode>(0));
        Assert.Equal(2, table.GetCount(new Handle<DummyNode>(0)));
    }

    [Fact]
    public void Decrement_FromTwo_SetsCountToOne_NoFree()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(1);
        var freed = new List<int>();
        table.Increment(new Handle<DummyNode>(0));
        table.Increment(new Handle<DummyNode>(0));
        var h0 = new Handle<DummyNode>(0);
        if (table.Decrement(h0))
            freed.Add(h0.Index);
        Assert.Equal(1, table.GetCount(new Handle<DummyNode>(0)));
        Assert.Empty(freed);
    }

    [Fact]
    public void Decrement_ToZero_FiresOnFreed()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(1);
        var freed = new List<int>();
        table.Increment(new Handle<DummyNode>(0));
        var h0 = new Handle<DummyNode>(0);
        if (table.Decrement(h0))
            freed.Add(h0.Index);
        Assert.Equal(0, table.GetCount(new Handle<DummyNode>(0)));
        Assert.Single(freed);
        Assert.Equal(0, freed[0]);
    }

    [Fact]
    public void GetCount_Uninitialized_ReturnsZero()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(2);
        table.Increment(new Handle<DummyNode>(0));
        Assert.Equal(0, table.GetCount(new Handle<DummyNode>(1)));
    }

    [Fact]
    public void MultipleIndices_Independent()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(3);
        table.Increment(new Handle<DummyNode>(0));
        table.Increment(new Handle<DummyNode>(0));
        table.Increment(new Handle<DummyNode>(1));
        table.Increment(new Handle<DummyNode>(2));
        table.Increment(new Handle<DummyNode>(2));
        table.Increment(new Handle<DummyNode>(2));

        Assert.Equal(2, table.GetCount(new Handle<DummyNode>(0)));
        Assert.Equal(1, table.GetCount(new Handle<DummyNode>(1)));
        Assert.Equal(3, table.GetCount(new Handle<DummyNode>(2)));
    }

    // ═══════════════════════════ Cascade-free: binary tree ════════════════

    [Fact]
    public void CascadeDecrement_SingleNode_FreesJustThatNode()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(1);
        var freed = new List<int>();
        table.Increment(new Handle<DummyNode>(0));

        var runner = new CascadeRunner(table);
        runner.Decrement(new Handle<DummyNode>(0), BinaryTreeCallback(runner, 0, freed));

        Assert.Equal(0, table.GetCount(new Handle<DummyNode>(0)));
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
            table.Increment(new Handle<DummyNode>(i));

        var runner = new CascadeRunner(table);
        runner.Decrement(new Handle<DummyNode>(0), BinaryTreeCallback(runner, 6, freed));

        for (var i = 0; i <= 6; i++)
            Assert.Equal(0, table.GetCount(new Handle<DummyNode>(i)));

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

        table.Increment(new Handle<DummyNode>(0));
        table.Increment(new Handle<DummyNode>(1));
        table.Increment(new Handle<DummyNode>(1)); // shared: two parents
        table.Increment(new Handle<DummyNode>(2));

        var runner = new CascadeRunner(table);
        runner.Decrement(new Handle<DummyNode>(0), SingleChildCallback(runner, 0, 1, freed));

        Assert.Equal(1, table.GetCount(new Handle<DummyNode>(1)));
        Assert.Single(freed);
        Assert.Equal(0, freed[0]);

        freed.Clear();
        runner.Decrement(new Handle<DummyNode>(2), SingleChildCallback(runner, 2, 1, freed));

        Assert.Equal(0, table.GetCount(new Handle<DummyNode>(1)));
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

        table.Increment(new Handle<DummyNode>(0));
        table.Increment(new Handle<DummyNode>(0));

        var runner = new CascadeRunner(table);
        runner.Decrement(new Handle<DummyNode>(0), BinaryTreeCallback(runner, 0, freed));

        Assert.Equal(1, table.GetCount(new Handle<DummyNode>(0)));
        Assert.Empty(freed);
    }

    // ═══════════════════════════ Cascade-free: linear chain ═══════════════

    [Fact]
    public void CascadeDecrement_LinearChain_FreesAll()
    {
        const int ChainLength = 100;
        var table = RefCountTable<DummyNode>.TestAccessor.Create(directoryLength: 4, slabShift: 5);
        table.EnsureCapacity(ChainLength);
        var freed = new List<int>();

        for (var i = 0; i < ChainLength; i++)
            table.Increment(new Handle<DummyNode>(i));

        var runner = new CascadeRunner(table);
        runner.Decrement(new Handle<DummyNode>(0), LinearChainCallback(runner, ChainLength - 1, freed));

        Assert.Equal(ChainLength, freed.Count);
        for (var i = 0; i < ChainLength; i++)
            Assert.Equal(0, table.GetCount(new Handle<DummyNode>(i)));
    }

    [Fact]
    public void CascadeDecrement_DeepChain_NoStackOverflow()
    {
        const int Depth = 10_000;
        var table = new RefCountTable<DummyNode>();
        table.EnsureCapacity(Depth);
        var freed = new List<int>();

        for (var i = 0; i < Depth; i++)
            table.Increment(new Handle<DummyNode>(i));

        var runner = new CascadeRunner(table);
        runner.Decrement(new Handle<DummyNode>(0), LinearChainCallback(runner, Depth - 1, freed));

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

        table.Increment(new Handle<DummyNode>(0));
        for (var i = 1; i <= Fanout; i++)
            table.Increment(new Handle<DummyNode>(i));

        var runner = new CascadeRunner(table);
        runner.Decrement(new Handle<DummyNode>(0), WideTreeCallback(runner, Fanout, freed));

        Assert.Equal(Fanout + 1, freed.Count);
        Assert.Contains(0, freed);
        for (var i = 1; i <= Fanout; i++)
        {
            Assert.Equal(0, table.GetCount(new Handle<DummyNode>(i)));
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
        var handles = new Handle<DummyNode>[indices.Length];
        for (var i = 0; i < indices.Length; i++)
            handles[i] = new Handle<DummyNode>(indices[i]);
        table.IncrementBatch(handles);

        Assert.Equal(3, table.GetCount(new Handle<DummyNode>(0)));
        Assert.Equal(2, table.GetCount(new Handle<DummyNode>(1)));
        Assert.Equal(1, table.GetCount(new Handle<DummyNode>(2)));
    }

    [Fact]
    public void DecrementBatch_DecrementsAll_FreesAtZero_WithCascade()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(3);
        var freed = new List<int>();

        table.Increment(new Handle<DummyNode>(0));
        table.Increment(new Handle<DummyNode>(0));
        table.Increment(new Handle<DummyNode>(1));
        table.Increment(new Handle<DummyNode>(2));

        int[] batch = [0, 1, 2];
        var batchHandles = new Handle<DummyNode>[batch.Length];
        for (var i = 0; i < batch.Length; i++)
            batchHandles[i] = new Handle<DummyNode>(batch[i]);
        var hitZero = new UnsafeStack<Handle<DummyNode>>();
        table.DecrementBatch(batchHandles, hitZero);
        while (hitZero.TryPop(out var freedHandle))
            freed.Add(freedHandle.Index);

        Assert.Equal(1, table.GetCount(new Handle<DummyNode>(0)));
        Assert.Equal(0, table.GetCount(new Handle<DummyNode>(1)));
        Assert.Equal(0, table.GetCount(new Handle<DummyNode>(2)));

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
            table.Increment(new Handle<DummyNode>(i));

        int[] batch = [0];
        var batchHandles = new Handle<DummyNode>[batch.Length];
        for (var i = 0; i < batch.Length; i++)
            batchHandles[i] = new Handle<DummyNode>(batch[i]);
        var runner = new CascadeRunner(table);
        runner.DecrementBatch(batchHandles, BinaryTreeCallback(runner, 6, freed));

        Assert.Equal(7, freed.Count);
        for (var i = 0; i <= 6; i++)
            Assert.Equal(0, table.GetCount(new Handle<DummyNode>(i)));
    }

    [Fact]
    public void DecrementBatch_Empty_DoesNothing()
    {
        var table = CreateTinyTable();
        var hitZero = new UnsafeStack<Handle<DummyNode>>();
        table.DecrementBatch([], hitZero);
        Assert.Equal(0, hitZero.Count);
    }

    // ═══════════════════════════ Slab boundaries ═════════════════════════

    [Fact]
    public void Increment_CrossesSlabBoundary()
    {
        var table = CreateTinyTable(directoryLength: 4, slabShift: 2);
        table.EnsureCapacity(5);
        var ta = table.GetTestAccessor();

        table.Increment(new Handle<DummyNode>(3));
        table.Increment(new Handle<DummyNode>(4));

        Assert.NotNull(ta.Directory[0]);
        Assert.NotNull(ta.Directory[1]);
        Assert.Equal(1, table.GetCount(new Handle<DummyNode>(3)));
        Assert.Equal(1, table.GetCount(new Handle<DummyNode>(4)));
    }

    // ═══════════════════════════ Default constructor ══════════════════════

    [Fact]
    public void DefaultConstructor_UsesExpectedParameters()
    {
        var table = new RefCountTable<DummyNode>();
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

        table.Increment(new Handle<DummyNode>(0));
        table.Increment(new Handle<DummyNode>(0));
        table.Increment(new Handle<DummyNode>(1));

        table.Decrement(new Handle<DummyNode>(0));
        Assert.Equal(1, table.GetCount(new Handle<DummyNode>(0)));

        table.Increment(new Handle<DummyNode>(0));
        Assert.Equal(2, table.GetCount(new Handle<DummyNode>(0)));

        var h0 = new Handle<DummyNode>(0);
        if (table.Decrement(h0))
            freed.Add(h0.Index);
        if (table.Decrement(h0))
            freed.Add(h0.Index);
        Assert.Equal(0, table.GetCount(new Handle<DummyNode>(0)));
        Assert.Single(freed);
    }

    [Fact]
    public void ManyIncrementsThenBatchDecrement()
    {
        var table = CreateTinyTable(directoryLength: 8, slabShift: 3);
        const int Count = 20;
        table.EnsureCapacity(Count);
        var freed = new List<int>();

        var handles = new Handle<DummyNode>[Count];
        for (var i = 0; i < Count; i++)
        {
            handles[i] = new Handle<DummyNode>(i);
            table.Increment(handles[i]);
        }

        var hitZero = new UnsafeStack<Handle<DummyNode>>();
        table.DecrementBatch(handles, hitZero);
        while (hitZero.TryPop(out var freedHandle))
            freed.Add(freedHandle.Index);

        Assert.Equal(Count, freed.Count);
        for (var i = 0; i < Count; i++)
            Assert.Equal(0, table.GetCount(new Handle<DummyNode>(i)));
    }

    // ═══════════════════════════ Two-table cross-cascade ═════════════════

    private sealed class TwoTableContext
    {
        public RefCountTable<DummyNode> TableA { get; set; } = null!;
        public RefCountTable<DummyNode> TableB { get; set; } = null!;
        public CascadeRunner RunnerA { get; set; } = null!;
        public CascadeRunner RunnerB { get; set; } = null!;
        public List<int> FreedA { get; } = [];
        public List<int> FreedB { get; } = [];
    }

    private static Action<Handle<DummyNode>> TwoTableACallback(TwoTableContext ctx)
    {
        void OnFreed(Handle<DummyNode> handle)
        {
            ctx.FreedA.Add(handle.Index);
            if (handle.Index == 0)
                ctx.RunnerB.Decrement(new Handle<DummyNode>(0), TwoTableBCallback(ctx));
        }
        return OnFreed;
    }

    private static Action<Handle<DummyNode>> TwoTableBCallback(TwoTableContext ctx)
    {
        void OnFreed(Handle<DummyNode> handle)
        {
            ctx.FreedB.Add(handle.Index);
            if (handle.Index == 0)
                ctx.RunnerA.Decrement(new Handle<DummyNode>(1), TwoTableACallback(ctx));
        }
        return OnFreed;
    }

    [Fact]
    public void TwoTable_BounceBack_CascadesCorrectly()
    {
        var ctx = new TwoTableContext
        {
            TableA = CreateTinyTable(directoryLength: 4, slabShift: 2),
            TableB = CreateTinyTable(directoryLength: 4, slabShift: 2),
        };
        ctx.RunnerA = new CascadeRunner(ctx.TableA);
        ctx.RunnerB = new CascadeRunner(ctx.TableB);
        ctx.TableA.EnsureCapacity(2);
        ctx.TableB.EnsureCapacity(1);

        ctx.TableA.Increment(new Handle<DummyNode>(0));
        ctx.TableA.Increment(new Handle<DummyNode>(1));
        ctx.TableB.Increment(new Handle<DummyNode>(0));

        ctx.RunnerA.Decrement(new Handle<DummyNode>(0), TwoTableACallback(ctx));

        Assert.Equal(0, ctx.TableA.GetCount(new Handle<DummyNode>(0)));
        Assert.Equal(0, ctx.TableA.GetCount(new Handle<DummyNode>(1)));
        Assert.Equal(0, ctx.TableB.GetCount(new Handle<DummyNode>(0)));

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
        ctx.RunnerA = new CascadeRunner(ctx.TableA);
        ctx.RunnerB = new CascadeRunner(ctx.TableB);
        ctx.TableA.EnsureCapacity(2);
        ctx.TableB.EnsureCapacity(1);

        ctx.TableA.Increment(new Handle<DummyNode>(0));
        ctx.TableA.Increment(new Handle<DummyNode>(1));
        ctx.TableA.Increment(new Handle<DummyNode>(1)); // A[1] = 2 (shared)
        ctx.TableB.Increment(new Handle<DummyNode>(0));

        ctx.RunnerA.Decrement(new Handle<DummyNode>(0), TwoTableACallback(ctx));

        Assert.Equal(0, ctx.TableA.GetCount(new Handle<DummyNode>(0)));
        Assert.Equal(1, ctx.TableA.GetCount(new Handle<DummyNode>(1))); // survived
        Assert.Equal(0, ctx.TableB.GetCount(new Handle<DummyNode>(0)));

        Assert.Single(ctx.FreedA);
        Assert.Equal(0, ctx.FreedA[0]);
        Assert.Single(ctx.FreedB);
        Assert.Equal(0, ctx.FreedB[0]);
    }

    // ═══════════════════════════ Three-table cross-cascade ═══════════════

    // Data-driven infrastructure for cross-table cascade tests with three tables (A=0, B=1, C=2).
    // Nodes are identified by (tableId, index). Edges define parent→child relationships across tables.

    private sealed class MultiTableContext
    {
        public const int A = 0, B = 1, C = 2;

        public RefCountTable<DummyNode>[] Tables { get; } = new RefCountTable<DummyNode>[3];
        public CascadeRunner[] Runners { get; }
        public List<int>[] Freed { get; } = [[], [], []];
        private readonly Dictionary<int, List<(int tableId, int index)>>[] _edges = [[], [], []];

        public MultiTableContext(int nodesPerTable = 8)
        {
            const int SlabShift = 3;
            var slabLength = 1 << SlabShift;
            var directoryLength = Math.Max(4, (nodesPerTable + slabLength - 1) / slabLength);

            this.Runners = new CascadeRunner[3];
            for (var tableIndex = 0; tableIndex < 3; tableIndex++)
            {
                this.Tables[tableIndex] = CreateTinyTable(directoryLength: directoryLength, slabShift: SlabShift);
                this.Tables[tableIndex].EnsureCapacity(nodesPerTable);
                this.Runners[tableIndex] = new CascadeRunner(this.Tables[tableIndex]);
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
            => this.Runners[tableId].Decrement(new Handle<DummyNode>(index), this.MakeCallback(tableId));

        private Action<Handle<DummyNode>> MakeCallback(int tableId)
        {
            void OnFreed(Handle<DummyNode> handle)
            {
                this.Freed[tableId].Add(handle.Index);
                var children = this.GetChildren(tableId, handle.Index);
                if (children == null) return;
                foreach (var (childTableId, childIndex) in children)
                    this.Runners[childTableId].Decrement(new Handle<DummyNode>(childIndex), this.MakeCallback(childTableId));
            }
            return OnFreed;
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

    /// <summary>All 27 (rootTable, sharedTable, uniqueTable) assignments across 3 tables.</summary>
    public static IEnumerable<object[]> AllThreeTableAssignments()
    {
        for (var root = 0; root < 3; root++)
            for (var shared = 0; shared < 3; shared++)
                for (var unique = 0; unique < 3; unique++)
                    yield return [root, shared, unique];
    }

    /// <summary>All 6 orderings for releasing 3 versions (0, 1, 2).</summary>
    public static IEnumerable<object[]> AllReleaseOrders()
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
        ctx.Tables[origin].Increment(new Handle<DummyNode>(0));

        foreach (var ct in childTables)
        {
            ctx.Tables[ct].Increment(new Handle<DummyNode>(1));
            ctx.AddEdge(origin, 0, ct, 1);
        }

        ctx.Decrement(origin, 0);

        Assert.Equal(0, ctx.Tables[origin].GetCount(new Handle<DummyNode>(0)));
        Assert.Contains(0, ctx.Freed[origin]);

        foreach (var ct in childTables)
        {
            Assert.Equal(0, ctx.Tables[ct].GetCount(new Handle<DummyNode>(1)));
            Assert.Contains(1, ctx.Freed[ct]);
        }

        for (var tableIndex = 0; tableIndex < 3; tableIndex++)
        {
            if (tableIndex == origin || childTables.Contains(tableIndex))
                continue;
            Assert.Empty(ctx.Freed[tableIndex]);
        }
    }

    [Theory]
    [MemberData(nameof(AllChildSubsets))]
    public void ThreeTable_SingleHop_SharedChildren_Survive(int origin, int[] childTables)
    {
        var ctx = new MultiTableContext();
        ctx.Tables[origin].Increment(new Handle<DummyNode>(0));

        foreach (var ct in childTables)
        {
            ctx.Tables[ct].Increment(new Handle<DummyNode>(1));
            ctx.Tables[ct].Increment(new Handle<DummyNode>(1)); // rc=2: shared by another parent
            ctx.AddEdge(origin, 0, ct, 1);
        }

        // Phase 1: cascade from root — children survive with rc=1
        ctx.Decrement(origin, 0);

        Assert.Equal(0, ctx.Tables[origin].GetCount(new Handle<DummyNode>(0)));
        Assert.Contains(0, ctx.Freed[origin]);

        foreach (var ct in childTables)
        {
            Assert.Equal(1, ctx.Tables[ct].GetCount(new Handle<DummyNode>(1)));
            Assert.DoesNotContain(1, ctx.Freed[ct]);
        }

        // Phase 2: explicitly free each child
        foreach (var ct in childTables)
            ctx.Decrement(ct, 1);

        foreach (var ct in childTables)
        {
            Assert.Equal(0, ctx.Tables[ct].GetCount(new Handle<DummyNode>(1)));
            Assert.Contains(1, ctx.Freed[ct]);
        }
    }

    // ── Chain through all three tables ───────────────────────────────────

    [Theory]
    [MemberData(nameof(AllThreeTableChainOrders))]
    public void ThreeTable_Chain_AllOrders(int first, int second, int third)
    {
        var ctx = new MultiTableContext();

        ctx.Tables[first].Increment(new Handle<DummyNode>(0));
        ctx.Tables[second].Increment(new Handle<DummyNode>(0));
        ctx.Tables[third].Increment(new Handle<DummyNode>(0));

        ctx.AddEdge(first, 0, second, 0);
        ctx.AddEdge(second, 0, third, 0);

        ctx.Decrement(first, 0);

        Assert.Equal(0, ctx.Tables[first].GetCount(new Handle<DummyNode>(0)));
        Assert.Equal(0, ctx.Tables[second].GetCount(new Handle<DummyNode>(0)));
        Assert.Equal(0, ctx.Tables[third].GetCount(new Handle<DummyNode>(0)));

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
        ctx.Tables[A].Increment(new Handle<DummyNode>(0));
        ctx.Tables[B].Increment(new Handle<DummyNode>(0));
        ctx.Tables[C].Increment(new Handle<DummyNode>(0));
        ctx.Tables[A].Increment(new Handle<DummyNode>(1));

        ctx.AddEdge(A, 0, B, 0);
        ctx.AddEdge(B, 0, C, 0);
        ctx.AddEdge(C, 0, A, 1);

        ctx.Decrement(A, 0);

        Assert.Equal(0, ctx.Tables[A].GetCount(new Handle<DummyNode>(0)));
        Assert.Equal(0, ctx.Tables[A].GetCount(new Handle<DummyNode>(1)));
        Assert.Equal(0, ctx.Tables[B].GetCount(new Handle<DummyNode>(0)));
        Assert.Equal(0, ctx.Tables[C].GetCount(new Handle<DummyNode>(0)));

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
        ctx.Tables[A].Increment(new Handle<DummyNode>(0));
        ctx.Tables[B].Increment(new Handle<DummyNode>(0));
        ctx.Tables[C].Increment(new Handle<DummyNode>(0));
        ctx.Tables[A].Increment(new Handle<DummyNode>(1));
        ctx.Tables[A].Increment(new Handle<DummyNode>(1)); // rc=2

        ctx.AddEdge(A, 0, B, 0);
        ctx.AddEdge(A, 0, C, 0);
        ctx.AddEdge(B, 0, A, 1);
        ctx.AddEdge(C, 0, A, 1);

        ctx.Decrement(A, 0);

        Assert.Equal(0, ctx.Tables[A].GetCount(new Handle<DummyNode>(0)));
        Assert.Equal(0, ctx.Tables[A].GetCount(new Handle<DummyNode>(1)));
        Assert.Equal(0, ctx.Tables[B].GetCount(new Handle<DummyNode>(0)));
        Assert.Equal(0, ctx.Tables[C].GetCount(new Handle<DummyNode>(0)));

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
        ctx.Tables[A].Increment(new Handle<DummyNode>(0));
        ctx.Tables[B].Increment(new Handle<DummyNode>(0));
        ctx.Tables[C].Increment(new Handle<DummyNode>(0));
        ctx.Tables[C].Increment(new Handle<DummyNode>(0)); // rc=2

        ctx.AddEdge(A, 0, C, 0);
        ctx.AddEdge(B, 0, C, 0);

        // First root: C[0] survives
        ctx.Decrement(A, 0);
        Assert.Equal(1, ctx.Tables[C].GetCount(new Handle<DummyNode>(0)));
        Assert.Empty(ctx.Freed[C]);

        // Second root: C[0] now frees
        ctx.Decrement(B, 0);
        Assert.Equal(0, ctx.Tables[C].GetCount(new Handle<DummyNode>(0)));
        Assert.Single(ctx.Freed[C]);
    }

    [Fact]
    public void ThreeTable_ComplexGraph_AllTablesInterconnected()
    {
        const int A = MultiTableContext.A, B = MultiTableContext.B, C = MultiTableContext.C;
        var ctx = new MultiTableContext();

        // A[0] → {B[0], C[0]}, B[0] → {A[1], C[1]}, C[0] → {A[2]}, C[1] → {B[1]}
        ctx.Tables[A].Increment(new Handle<DummyNode>(0));
        ctx.Tables[A].Increment(new Handle<DummyNode>(1));
        ctx.Tables[A].Increment(new Handle<DummyNode>(2));
        ctx.Tables[B].Increment(new Handle<DummyNode>(0));
        ctx.Tables[B].Increment(new Handle<DummyNode>(1));
        ctx.Tables[C].Increment(new Handle<DummyNode>(0));
        ctx.Tables[C].Increment(new Handle<DummyNode>(1));

        ctx.AddEdge(A, 0, B, 0);
        ctx.AddEdge(A, 0, C, 0);
        ctx.AddEdge(B, 0, A, 1);
        ctx.AddEdge(B, 0, C, 1);
        ctx.AddEdge(C, 0, A, 2);
        ctx.AddEdge(C, 1, B, 1);

        ctx.Decrement(A, 0);

        for (var i = 0; i < 3; i++)
            Assert.Equal(0, ctx.Tables[A].GetCount(new Handle<DummyNode>(i)));
        for (var i = 0; i < 2; i++)
            Assert.Equal(0, ctx.Tables[B].GetCount(new Handle<DummyNode>(i)));
        for (var i = 0; i < 2; i++)
            Assert.Equal(0, ctx.Tables[C].GetCount(new Handle<DummyNode>(i)));

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
        ctx.Tables[A].Increment(new Handle<DummyNode>(0));
        ctx.Tables[A].Increment(new Handle<DummyNode>(1));
        ctx.Tables[B].Increment(new Handle<DummyNode>(0));
        ctx.Tables[B].Increment(new Handle<DummyNode>(1));
        ctx.Tables[C].Increment(new Handle<DummyNode>(0));
        ctx.Tables[C].Increment(new Handle<DummyNode>(0)); // rc=2

        ctx.AddEdge(A, 0, B, 0);
        ctx.AddEdge(A, 1, B, 1);
        ctx.AddEdge(B, 0, C, 0);
        ctx.AddEdge(B, 1, C, 0);

        // First root: C[0] survives
        ctx.Decrement(A, 0);
        Assert.Equal(1, ctx.Tables[C].GetCount(new Handle<DummyNode>(0)));
        Assert.Empty(ctx.Freed[C]);

        // Second root: C[0] now frees
        ctx.Decrement(A, 1);
        Assert.Equal(0, ctx.Tables[C].GetCount(new Handle<DummyNode>(0)));
        Assert.Single(ctx.Freed[C]);
    }

    [Fact]
    public void ThreeTable_MultipleChildrenInSameTable()
    {
        const int A = MultiTableContext.A, B = MultiTableContext.B;
        var ctx = new MultiTableContext();

        // A[0] → {B[0], B[1], B[2]}: three children in same target table
        ctx.Tables[A].Increment(new Handle<DummyNode>(0));
        ctx.Tables[B].Increment(new Handle<DummyNode>(0));
        ctx.Tables[B].Increment(new Handle<DummyNode>(1));
        ctx.Tables[B].Increment(new Handle<DummyNode>(2));

        ctx.AddEdge(A, 0, B, 0);
        ctx.AddEdge(A, 0, B, 1);
        ctx.AddEdge(A, 0, B, 2);

        ctx.Decrement(A, 0);

        Assert.Equal(0, ctx.Tables[A].GetCount(new Handle<DummyNode>(0)));
        Assert.Equal(0, ctx.Tables[B].GetCount(new Handle<DummyNode>(0)));
        Assert.Equal(0, ctx.Tables[B].GetCount(new Handle<DummyNode>(1)));
        Assert.Equal(0, ctx.Tables[B].GetCount(new Handle<DummyNode>(2)));

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
            ctx.Tables[tableOrder[i % 3]].Increment(new Handle<DummyNode>(i / 3));

        for (var i = 0; i < Depth - 1; i++)
            ctx.AddEdge(tableOrder[i % 3], i / 3, tableOrder[(i + 1) % 3], (i + 1) / 3);

        ctx.Decrement(MultiTableContext.A, 0);

        for (var i = 0; i < Depth; i++)
            Assert.Equal(0, ctx.Tables[tableOrder[i % 3]].GetCount(new Handle<DummyNode>(i / 3)));

        var totalFreed = ctx.Freed[0].Count + ctx.Freed[1].Count + ctx.Freed[2].Count;
        Assert.Equal(Depth, totalFreed);
        Assert.Equal(10, ctx.Freed[0].Count);
        Assert.Equal(10, ctx.Freed[1].Count);
        Assert.Equal(10, ctx.Freed[2].Count);
    }

    // ═══════════════════════════ Persistent data structures ═══════════════

    // Tests modeling persistent/functional trees with structural sharing.
    // Multiple "versions" (roots) share subtrees. Releasing one version frees only the unshared parts.
    // When the last version referencing a shared subtree is released, it cascades through.

    // ── Two versions, shared subtree: all table assignments ──────────────

    [Theory]
    [MemberData(nameof(AllThreeTableAssignments))]
    public void Persistent_TwoVersions_SharedSubtree(int rootT, int sharedT, int uniqueT)
    {
        var ctx = new MultiTableContext();

        // Index layout (non-overlapping so same-table assignments work):
        // Roots: 0, 1. Unique children: 2, 3. Shared subtree: 4, 5, 6.
        //
        // V1: root[rootT,0] → { unique[uniqueT,2], shared[sharedT,4] }
        // V2: root[rootT,1] → { unique[uniqueT,3], shared[sharedT,4] }
        // shared[sharedT,4] → { shared[sharedT,5], shared[sharedT,6] }

        ctx.Tables[rootT].Increment(new Handle<DummyNode>(0));
        ctx.Tables[rootT].Increment(new Handle<DummyNode>(1));
        ctx.Tables[uniqueT].Increment(new Handle<DummyNode>(2));
        ctx.Tables[uniqueT].Increment(new Handle<DummyNode>(3));
        ctx.Tables[sharedT].Increment(new Handle<DummyNode>(4));
        ctx.Tables[sharedT].Increment(new Handle<DummyNode>(4)); // rc=2 (two version roots point here)
        ctx.Tables[sharedT].Increment(new Handle<DummyNode>(5));
        ctx.Tables[sharedT].Increment(new Handle<DummyNode>(6));

        ctx.AddEdge(rootT, 0, uniqueT, 2);
        ctx.AddEdge(rootT, 0, sharedT, 4);
        ctx.AddEdge(rootT, 1, uniqueT, 3);
        ctx.AddEdge(rootT, 1, sharedT, 4);
        ctx.AddEdge(sharedT, 4, sharedT, 5);
        ctx.AddEdge(sharedT, 4, sharedT, 6);

        // Release V1: root + unique freed, shared subtree survives
        ctx.Decrement(rootT, 0);

        Assert.Equal(0, ctx.Tables[rootT].GetCount(new Handle<DummyNode>(0)));
        Assert.Equal(0, ctx.Tables[uniqueT].GetCount(new Handle<DummyNode>(2)));
        Assert.Equal(1, ctx.Tables[sharedT].GetCount(new Handle<DummyNode>(4))); // survived (rc=1)
        Assert.Equal(1, ctx.Tables[sharedT].GetCount(new Handle<DummyNode>(5))); // untouched
        Assert.Equal(1, ctx.Tables[sharedT].GetCount(new Handle<DummyNode>(6))); // untouched

        // Release V2: everything goes
        ctx.Decrement(rootT, 1);

        Assert.Equal(0, ctx.Tables[rootT].GetCount(new Handle<DummyNode>(1)));
        Assert.Equal(0, ctx.Tables[uniqueT].GetCount(new Handle<DummyNode>(3)));
        Assert.Equal(0, ctx.Tables[sharedT].GetCount(new Handle<DummyNode>(4)));
        Assert.Equal(0, ctx.Tables[sharedT].GetCount(new Handle<DummyNode>(5)));
        Assert.Equal(0, ctx.Tables[sharedT].GetCount(new Handle<DummyNode>(6)));
    }

    // ── Three versions, shared node: all table assignments ───────────────

    [Theory]
    [MemberData(nameof(AllThreeTableAssignments))]
    public void Persistent_ThreeVersions_SharedNode(int rootT, int sharedT, int uniqueT)
    {
        var ctx = new MultiTableContext();

        // V1: root[rootT,0] → { unique[uniqueT,3], shared[sharedT,6] }
        // V2: root[rootT,1] → { unique[uniqueT,4], shared[sharedT,6] }
        // V3: root[rootT,2] → { unique[uniqueT,5], shared[sharedT,6] }
        // shared[sharedT,6] → shared[sharedT,7]

        ctx.Tables[rootT].Increment(new Handle<DummyNode>(0));
        ctx.Tables[rootT].Increment(new Handle<DummyNode>(1));
        ctx.Tables[rootT].Increment(new Handle<DummyNode>(2));
        ctx.Tables[uniqueT].Increment(new Handle<DummyNode>(3));
        ctx.Tables[uniqueT].Increment(new Handle<DummyNode>(4));
        ctx.Tables[uniqueT].Increment(new Handle<DummyNode>(5));
        ctx.Tables[sharedT].Increment(new Handle<DummyNode>(6));
        ctx.Tables[sharedT].Increment(new Handle<DummyNode>(6));
        ctx.Tables[sharedT].Increment(new Handle<DummyNode>(6)); // rc=3
        ctx.Tables[sharedT].Increment(new Handle<DummyNode>(7));

        for (var v = 0; v < 3; v++)
        {
            ctx.AddEdge(rootT, v, uniqueT, 3 + v);
            ctx.AddEdge(rootT, v, sharedT, 6);
        }

        ctx.AddEdge(sharedT, 6, sharedT, 7);

        // Release V1: shared rc=3→2
        ctx.Decrement(rootT, 0);
        Assert.Equal(0, ctx.Tables[rootT].GetCount(new Handle<DummyNode>(0)));
        Assert.Equal(0, ctx.Tables[uniqueT].GetCount(new Handle<DummyNode>(3)));
        Assert.Equal(2, ctx.Tables[sharedT].GetCount(new Handle<DummyNode>(6)));
        Assert.Equal(1, ctx.Tables[sharedT].GetCount(new Handle<DummyNode>(7)));

        // Release V2: shared rc=2→1
        ctx.Decrement(rootT, 1);
        Assert.Equal(0, ctx.Tables[rootT].GetCount(new Handle<DummyNode>(1)));
        Assert.Equal(0, ctx.Tables[uniqueT].GetCount(new Handle<DummyNode>(4)));
        Assert.Equal(1, ctx.Tables[sharedT].GetCount(new Handle<DummyNode>(6)));
        Assert.Equal(1, ctx.Tables[sharedT].GetCount(new Handle<DummyNode>(7)));

        // Release V3: shared rc=1→0 → cascade frees shared + child
        ctx.Decrement(rootT, 2);
        Assert.Equal(0, ctx.Tables[rootT].GetCount(new Handle<DummyNode>(2)));
        Assert.Equal(0, ctx.Tables[uniqueT].GetCount(new Handle<DummyNode>(5)));
        Assert.Equal(0, ctx.Tables[sharedT].GetCount(new Handle<DummyNode>(6)));
        Assert.Equal(0, ctx.Tables[sharedT].GetCount(new Handle<DummyNode>(7)));
    }

    // ── Three versions, all release orders ───────────────────────────────

    [Theory]
    [MemberData(nameof(AllReleaseOrders))]
    public void Persistent_ThreeVersions_AllReleaseOrders(int first, int second, int third)
    {
        const int A = MultiTableContext.A, B = MultiTableContext.B, C = MultiTableContext.C;
        var ctx = new MultiTableContext();

        // Cross-table: roots in A, unique children in B, shared subtree in C.
        // V0: A[0] → { B[0], C[0] }
        // V1: A[1] → { B[1], C[0] }
        // V2: A[2] → { B[2], C[0] }
        // C[0] → C[1]
        // C[0] rc=3.

        for (var v = 0; v < 3; v++)
        {
            ctx.Tables[A].Increment(new Handle<DummyNode>(v));
            ctx.Tables[B].Increment(new Handle<DummyNode>(v));
            ctx.AddEdge(A, v, B, v);
            ctx.AddEdge(A, v, C, 0);
        }

        ctx.Tables[C].Increment(new Handle<DummyNode>(0));
        ctx.Tables[C].Increment(new Handle<DummyNode>(0));
        ctx.Tables[C].Increment(new Handle<DummyNode>(0)); // rc=3
        ctx.Tables[C].Increment(new Handle<DummyNode>(1));
        ctx.AddEdge(C, 0, C, 1);

        var order = new[] { first, second, third };
        for (var step = 0; step < 3; step++)
        {
            var v = order[step];
            ctx.Decrement(A, v);

            Assert.Equal(0, ctx.Tables[A].GetCount(new Handle<DummyNode>(v)));
            Assert.Equal(0, ctx.Tables[B].GetCount(new Handle<DummyNode>(v)));

            var expectedSharedRc = 2 - step;
            Assert.Equal(expectedSharedRc, ctx.Tables[C].GetCount(new Handle<DummyNode>(0)));
            Assert.Equal(expectedSharedRc > 0 ? 1 : 0, ctx.Tables[C].GetCount(new Handle<DummyNode>(1)));
        }
    }

    // ── Persistent linked list with shared spine across tables ───────────

    [Fact]
    public void PersistentList_SharedSpine_CrossTable()
    {
        const int A = MultiTableContext.A, B = MultiTableContext.B, C = MultiTableContext.C;
        var ctx = new MultiTableContext(nodesPerTable: 10);

        // Three versions of a persistent linked list with structural sharing.
        // Spine nodes alternate across tables for cross-table exercise:
        //   a=A[0] → b=B[0] → c=C[0] → d=A[1] → e=B[1]
        //
        // V1 (root C[1]) → a  (full list: a→b→c→d→e)
        // V2 (root C[2]) → c  (shares tail: c→d→e)
        // V3 (root C[3]) → e  (shares just the leaf)
        //
        // Refcounts: a=1, b=1, c=2 (from b and V2), d=1, e=2 (from d and V3)

        ctx.Tables[C].Increment(new Handle<DummyNode>(1)); // V1 root
        ctx.Tables[C].Increment(new Handle<DummyNode>(2)); // V2 root
        ctx.Tables[C].Increment(new Handle<DummyNode>(3)); // V3 root
        ctx.Tables[A].Increment(new Handle<DummyNode>(0)); // a
        ctx.Tables[B].Increment(new Handle<DummyNode>(0)); // b
        ctx.Tables[C].Increment(new Handle<DummyNode>(0)); // c (rc will be 2)
        ctx.Tables[C].Increment(new Handle<DummyNode>(0));
        ctx.Tables[A].Increment(new Handle<DummyNode>(1)); // d
        ctx.Tables[B].Increment(new Handle<DummyNode>(1)); // e (rc will be 2)
        ctx.Tables[B].Increment(new Handle<DummyNode>(1));

        ctx.AddEdge(C, 1, A, 0); // V1 → a
        ctx.AddEdge(C, 2, C, 0); // V2 → c
        ctx.AddEdge(C, 3, B, 1); // V3 → e
        ctx.AddEdge(A, 0, B, 0); // a → b
        ctx.AddEdge(B, 0, C, 0); // b → c
        ctx.AddEdge(C, 0, A, 1); // c → d
        ctx.AddEdge(A, 1, B, 1); // d → e

        // Release V1: frees V1 root, a, b. c survives (rc=2→1).
        ctx.Decrement(C, 1);
        Assert.Equal(0, ctx.Tables[A].GetCount(new Handle<DummyNode>(0))); // a freed
        Assert.Equal(0, ctx.Tables[B].GetCount(new Handle<DummyNode>(0))); // b freed
        Assert.Equal(1, ctx.Tables[C].GetCount(new Handle<DummyNode>(0))); // c survived
        Assert.Equal(1, ctx.Tables[A].GetCount(new Handle<DummyNode>(1))); // d untouched
        Assert.Equal(2, ctx.Tables[B].GetCount(new Handle<DummyNode>(1))); // e untouched (rc=2)

        // Release V2: frees V2 root, c, d. e survives (rc=2→1).
        ctx.Decrement(C, 2);
        Assert.Equal(0, ctx.Tables[C].GetCount(new Handle<DummyNode>(0))); // c freed
        Assert.Equal(0, ctx.Tables[A].GetCount(new Handle<DummyNode>(1))); // d freed (cascaded through c)
        Assert.Equal(1, ctx.Tables[B].GetCount(new Handle<DummyNode>(1))); // e survived (rc=1)

        // Release V3: frees V3 root, e. Everything gone.
        ctx.Decrement(C, 3);
        Assert.Equal(0, ctx.Tables[B].GetCount(new Handle<DummyNode>(1))); // e freed

        var totalFreed = ctx.Freed[A].Count + ctx.Freed[B].Count + ctx.Freed[C].Count;
        Assert.Equal(8, totalFreed); // 3 roots + 5 spine nodes
    }

    // ── Persistent binary tree with layered sharing across tables ────────

    [Fact]
    public void PersistentBinaryTree_ThreeVersions_LayeredSharing()
    {
        const int A = MultiTableContext.A, B = MultiTableContext.B, C = MultiTableContext.C;
        var ctx = new MultiTableContext(nodesPerTable: 10);

        // Depth-2 binary tree with 3 versions. Roots in A, left subtrees in B, right subtrees in C.
        //
        // V1 (original):     A[0]          V2 (new left):      A[1]          V3 (new right):    A[2]
        //                   /    \                             /    \                            /    \
        //                B[0]   C[0]                        B[1]   C[0]←shared              B[0]←shared  C[1]
        //               / \     / \                        / \     / \                      / \         / \
        //            B[2] B[3] C[2] C[3]                B[4] B[5] C[2] C[3]              B[2] B[3]   C[4] C[5]
        //
        // Shared: B[0] rc=2 (V1 + V3), C[0] rc=2 (V1 + V2)
        // Leaf nodes under shared subtrees have rc=1 — they cascade when their parent is freed.

        // -- V1 tree --
        ctx.Tables[A].Increment(new Handle<DummyNode>(0));
        ctx.Tables[B].Increment(new Handle<DummyNode>(0));
        ctx.Tables[B].Increment(new Handle<DummyNode>(0)); // B[0] rc=2 (shared with V3)
        ctx.Tables[C].Increment(new Handle<DummyNode>(0));
        ctx.Tables[C].Increment(new Handle<DummyNode>(0)); // C[0] rc=2 (shared with V2)
        ctx.Tables[B].Increment(new Handle<DummyNode>(2));
        ctx.Tables[B].Increment(new Handle<DummyNode>(3));
        ctx.Tables[C].Increment(new Handle<DummyNode>(2));
        ctx.Tables[C].Increment(new Handle<DummyNode>(3));

        ctx.AddEdge(A, 0, B, 0);
        ctx.AddEdge(A, 0, C, 0);
        ctx.AddEdge(B, 0, B, 2);
        ctx.AddEdge(B, 0, B, 3);
        ctx.AddEdge(C, 0, C, 2);
        ctx.AddEdge(C, 0, C, 3);

        // -- V2 tree (shares right subtree C[0]) --
        ctx.Tables[A].Increment(new Handle<DummyNode>(1));
        ctx.Tables[B].Increment(new Handle<DummyNode>(1));
        ctx.Tables[B].Increment(new Handle<DummyNode>(4));
        ctx.Tables[B].Increment(new Handle<DummyNode>(5));

        ctx.AddEdge(A, 1, B, 1);
        ctx.AddEdge(A, 1, C, 0);
        ctx.AddEdge(B, 1, B, 4);
        ctx.AddEdge(B, 1, B, 5);

        // -- V3 tree (shares left subtree B[0]) --
        ctx.Tables[A].Increment(new Handle<DummyNode>(2));
        ctx.Tables[C].Increment(new Handle<DummyNode>(1));
        ctx.Tables[C].Increment(new Handle<DummyNode>(4));
        ctx.Tables[C].Increment(new Handle<DummyNode>(5));

        ctx.AddEdge(A, 2, B, 0);
        ctx.AddEdge(A, 2, C, 1);
        ctx.AddEdge(C, 1, C, 4);
        ctx.AddEdge(C, 1, C, 5);

        // Release V1: only root freed. Both shared subtrees survive.
        ctx.Decrement(A, 0);

        Assert.Equal(0, ctx.Tables[A].GetCount(new Handle<DummyNode>(0)));
        Assert.Equal(1, ctx.Tables[B].GetCount(new Handle<DummyNode>(0))); // survived (rc=1)
        Assert.Equal(1, ctx.Tables[C].GetCount(new Handle<DummyNode>(0))); // survived (rc=1)
        Assert.Equal(1, ctx.Tables[B].GetCount(new Handle<DummyNode>(2))); // untouched (child of B[0])
        Assert.Equal(1, ctx.Tables[B].GetCount(new Handle<DummyNode>(3))); // untouched
        Assert.Equal(1, ctx.Tables[C].GetCount(new Handle<DummyNode>(2))); // untouched (child of C[0])
        Assert.Equal(1, ctx.Tables[C].GetCount(new Handle<DummyNode>(3))); // untouched

        // Release V2: V2's unique branch + shared C[0] subtree freed.
        ctx.Decrement(A, 1);

        Assert.Equal(0, ctx.Tables[A].GetCount(new Handle<DummyNode>(1)));
        Assert.Equal(0, ctx.Tables[B].GetCount(new Handle<DummyNode>(1))); // V2 unique
        Assert.Equal(0, ctx.Tables[B].GetCount(new Handle<DummyNode>(4))); // V2 unique leaf
        Assert.Equal(0, ctx.Tables[B].GetCount(new Handle<DummyNode>(5))); // V2 unique leaf
        Assert.Equal(0, ctx.Tables[C].GetCount(new Handle<DummyNode>(0))); // shared freed (rc=0)
        Assert.Equal(0, ctx.Tables[C].GetCount(new Handle<DummyNode>(2))); // cascaded
        Assert.Equal(0, ctx.Tables[C].GetCount(new Handle<DummyNode>(3))); // cascaded
        Assert.Equal(1, ctx.Tables[B].GetCount(new Handle<DummyNode>(0))); // V3's shared B[0] still alive
        Assert.Equal(1, ctx.Tables[B].GetCount(new Handle<DummyNode>(2))); // its children still alive
        Assert.Equal(1, ctx.Tables[B].GetCount(new Handle<DummyNode>(3)));

        // Release V3: V3's unique branch + shared B[0] subtree freed. Everything gone.
        ctx.Decrement(A, 2);

        Assert.Equal(0, ctx.Tables[A].GetCount(new Handle<DummyNode>(2)));
        Assert.Equal(0, ctx.Tables[B].GetCount(new Handle<DummyNode>(0))); // shared freed (rc=0)
        Assert.Equal(0, ctx.Tables[B].GetCount(new Handle<DummyNode>(2))); // cascaded
        Assert.Equal(0, ctx.Tables[B].GetCount(new Handle<DummyNode>(3))); // cascaded
        Assert.Equal(0, ctx.Tables[C].GetCount(new Handle<DummyNode>(1))); // V3 unique
        Assert.Equal(0, ctx.Tables[C].GetCount(new Handle<DummyNode>(4))); // V3 unique leaf
        Assert.Equal(0, ctx.Tables[C].GetCount(new Handle<DummyNode>(5))); // V3 unique leaf

        // 15 total nodes, each freed exactly once
        var totalFreed = ctx.Freed[A].Count + ctx.Freed[B].Count + ctx.Freed[C].Count;
        Assert.Equal(15, totalFreed);
        Assert.Equal(3, ctx.Freed[A].Count);
        Assert.Equal(6, ctx.Freed[B].Count);
        Assert.Equal(6, ctx.Freed[C].Count);
    }

    // ── Persistent tree harness ──────────────────────────────────────────
    // Programmatic infrastructure for building persistent binary trees with path-copy sharing,
    // adding roots at any node, releasing in any order, and verifying refcounts via a computed
    // reference model (topological propagation from active roots).

    private sealed class PersistentTreeHarness(MultiTableContext ctx)
    {
        private readonly MultiTableContext _ctx = ctx;
        private readonly List<(int TableId, int NodeIndex)> _allNodes = [];
        private readonly Dictionary<(int TableId, int NodeIndex), List<(int TableId, int NodeIndex)>> _children = [];
        private readonly List<(int TableId, int NodeIndex)> _activeRoots = [];
        private int _nextIndex;

        public (int TableId, int NodeIndex) AllocNode(int tableId)
        {
            var node = (tableId, _nextIndex++);
            _allNodes.Add(node);
            return node;
        }

        public void AddEdge((int TableId, int NodeIndex) parent, (int TableId, int NodeIndex) child)
        {
            if (!_children.TryGetValue(parent, out var list))
            {
                list = [];
                _children[parent] = list;
            }

            list.Add(child);
            _ctx.AddEdge(parent.TableId, parent.NodeIndex, child.TableId, child.NodeIndex);
            _ctx.Tables[child.TableId].Increment(new Handle<DummyNode>(child.NodeIndex));
        }

        public void AddRoot((int TableId, int NodeIndex) node)
        {
            _activeRoots.Add(node);
            _ctx.Tables[node.TableId].Increment(new Handle<DummyNode>(node.NodeIndex));
        }

        public void ReleaseRoot((int TableId, int NodeIndex) node)
        {
            Assert.True(_activeRoots.Remove(node), $"Root ({node.TableId},{node.NodeIndex}) not found");
            _ctx.Decrement(node.TableId, node.NodeIndex);
        }

        /// <summary>Builds a complete binary tree of the given depth, distributing nodes round-robin across 3 tables.
        /// Returns heap-order array: node i has children at 2i+1 and 2i+2.</summary>
        public (int TableId, int NodeIndex)[] BuildCompleteBinaryTree(int depth)
        {
            var count = (1 << (depth + 1)) - 1;
            var nodes = new (int TableId, int NodeIndex)[count];

            for (var n = 0; n < count; n++)
                nodes[n] = this.AllocNode(n % 3);

            for (var n = 0; n < count; n++)
            {
                var left = (2 * n) + 1;
                var right = (2 * n) + 2;
                if (left < count) this.AddEdge(nodes[n], nodes[left]);
                if (right < count) this.AddEdge(nodes[n], nodes[right]);
            }

            return nodes;
        }

        /// <summary>Creates a new version by path-copying from root to the given heap-order position.
        /// New nodes are created along the path; children not on the path are shared from the original.
        /// Returns a new tree array (clone of source with path positions replaced by new nodes).</summary>
        public (int TableId, int NodeIndex)[] PathCopy((int TableId, int NodeIndex)[] tree, int targetPosition)
        {
            Assert.True(targetPosition >= 0 && targetPosition < tree.Length);

            var path = new List<int>();
            var pos = targetPosition;
            while (pos > 0)
            {
                path.Add(pos);
                pos = (pos - 1) / 2;
            }

            path.Add(0);
            path.Reverse();

            var newTree = ((int TableId, int NodeIndex)[])tree.Clone();
            foreach (var heapIndex in path)
                newTree[heapIndex] = this.AllocNode(_nextIndex % 3);

            foreach (var heapIndex in path)
            {
                var left = (2 * heapIndex) + 1;
                var right = (2 * heapIndex) + 2;
                if (left < tree.Length) this.AddEdge(newTree[heapIndex], newTree[left]);
                if (right < tree.Length) this.AddEdge(newTree[heapIndex], newTree[right]);
            }

            return newTree;
        }

        /// <summary>Verifies all actual refcounts match expected values computed from the current active roots
        /// and topology via topological propagation.</summary>
        public void Verify()
        {
            var expected = this.ComputeExpected();
            foreach (var node in _allNodes)
            {
                var exp = expected.GetValueOrDefault(node);
                var actual = _ctx.Tables[node.TableId].GetCount(new Handle<DummyNode>(node.NodeIndex));
                Assert.Equal(exp, actual);
            }
        }

        private Dictionary<(int TableId, int NodeIndex), int> ComputeExpected()
        {
            var expectedRefCounts = new Dictionary<(int TableId, int NodeIndex), int>();

            foreach (var root in _activeRoots)
                expectedRefCounts[root] = expectedRefCounts.GetValueOrDefault(root) + 1;

            foreach (var node in this.TopoSort())
            {
                if (expectedRefCounts.GetValueOrDefault(node) <= 0) continue;
                if (!_children.TryGetValue(node, out var childList)) continue;
                foreach (var child in childList)
                    expectedRefCounts[child] = expectedRefCounts.GetValueOrDefault(child) + 1;
            }

            return expectedRefCounts;
        }

        private List<(int TableId, int NodeIndex)> TopoSort()
        {
            var visited = new HashSet<(int TableId, int NodeIndex)>();
            var order = new List<(int TableId, int NodeIndex)>();
            foreach (var node in _allNodes)
                this.TopoVisit(node, visited, order);
            order.Reverse();
            return order;
        }

        private void TopoVisit((int TableId, int NodeIndex) node, HashSet<(int TableId, int NodeIndex)> visited, List<(int TableId, int NodeIndex)> order)
        {
            if (!visited.Add(node)) return;
            if (_children.TryGetValue(node, out var childList))
                foreach (var child in childList)
                    this.TopoVisit(child, visited, order);
            order.Add(node);
        }
    }

    // ── Permutation helpers ──────────────────────────────────────────────

    private static IEnumerable<int[]> AllPermutations(int n)
    {
        var perm = new int[n];
        for (var i = 0; i < n; i++) perm[i] = i;
        do
        {
            yield return (int[])perm.Clone();
        }
        while (NextPermutation(perm));
    }

    private static bool NextPermutation(int[] arr)
    {
        var n = arr.Length;
        var i = n - 2;
        while (i >= 0 && arr[i] >= arr[i + 1]) i--;
        if (i < 0) return false;
        var j = n - 1;
        while (arr[j] <= arr[i]) j--;
        (arr[i], arr[j]) = (arr[j], arr[i]);
        Array.Reverse(arr, i + 1, n - i - 1);
        return true;
    }

    private static IEnumerable<int[]> SampledPermutations(int n, int count, int seed)
    {
        var forward = new int[n];
        var reverse = new int[n];
        for (var i = 0; i < n; i++)
        {
            forward[i] = i;
            reverse[i] = n - 1 - i;
        }

        yield return forward;
        yield return reverse;

        var rng = new Random(seed);
        for (var i = 0; i < count - 2; i++)
        {
            var perm = (int[])forward.Clone();
            rng.Shuffle(perm);
            yield return perm;
        }
    }

    public static IEnumerable<object[]> PermutationsOf4()
    {
        foreach (var perm in AllPermutations(4))
            yield return [perm];
    }

    public static IEnumerable<object[]> PermutationsOf5()
    {
        foreach (var perm in AllPermutations(5))
            yield return [perm];
    }

    public static IEnumerable<object[]> SampledPermutationsOf6()
    {
        foreach (var perm in SampledPermutations(6, 50, seed: 42))
            yield return [perm];
    }

    // ── Programmatic persistent tree tests ───────────────────────────────

    [Theory]
    [MemberData(nameof(PermutationsOf4))]
    public void Persistent_Depth3_FourVersions_AllReleaseOrders(int[] order)
    {
        var ctx = new MultiTableContext(nodesPerTable: 64);
        var harness = new PersistentTreeHarness(ctx);

        var v1 = harness.BuildCompleteBinaryTree(3); // 15 nodes
        harness.AddRoot(v1[0]);

        var v2 = harness.PathCopy(v1, 3);  // modify left-left leaf
        harness.AddRoot(v2[0]);

        var v3 = harness.PathCopy(v1, 5);  // modify right-left leaf
        harness.AddRoot(v3[0]);

        var v4 = harness.PathCopy(v2, 10); // modify right-side leaf from V2
        harness.AddRoot(v4[0]);

        harness.Verify();

        var roots = new[] { v1[0], v2[0], v3[0], v4[0] };
        foreach (var releaseOrderIndex in order)
        {
            harness.ReleaseRoot(roots[releaseOrderIndex]);
            harness.Verify();
        }
    }

    [Theory]
    [MemberData(nameof(PermutationsOf5))]
    public void Persistent_Depth3_FiveRootsWithInterior_AllReleaseOrders(int[] order)
    {
        var ctx = new MultiTableContext(nodesPerTable: 64);
        var harness = new PersistentTreeHarness(ctx);

        var v1 = harness.BuildCompleteBinaryTree(3);
        harness.AddRoot(v1[0]);

        var v2 = harness.PathCopy(v1, 4);
        harness.AddRoot(v2[0]);

        var v3 = harness.PathCopy(v1, 6);
        harness.AddRoot(v3[0]);

        harness.AddRoot(v1[1]);  // interior root: left subtree of V1
        harness.AddRoot(v2[2]);  // interior root: right subtree of V2

        harness.Verify();

        var roots = new[] { v1[0], v2[0], v3[0], v1[1], v2[2] };
        foreach (var releaseOrderIndex in order)
        {
            harness.ReleaseRoot(roots[releaseOrderIndex]);
            harness.Verify();
        }
    }

    [Theory]
    [MemberData(nameof(PermutationsOf4))]
    public void Persistent_Depth3_ChainedPathCopies_AllReleaseOrders(int[] order)
    {
        var ctx = new MultiTableContext(nodesPerTable: 64);
        var harness = new PersistentTreeHarness(ctx);

        // Each version is a path-copy of the previous — chained modifications
        var v1 = harness.BuildCompleteBinaryTree(3);
        harness.AddRoot(v1[0]);

        var v2 = harness.PathCopy(v1, 3);
        harness.AddRoot(v2[0]);

        var v3 = harness.PathCopy(v2, 6);
        harness.AddRoot(v3[0]);

        var v4 = harness.PathCopy(v3, 4);
        harness.AddRoot(v4[0]);

        harness.Verify();

        var roots = new[] { v1[0], v2[0], v3[0], v4[0] };
        foreach (var releaseOrderIndex in order)
        {
            harness.ReleaseRoot(roots[releaseOrderIndex]);
            harness.Verify();
        }
    }

    [Theory]
    [MemberData(nameof(SampledPermutationsOf6))]
    public void Persistent_Depth4_SixVersions_SampledOrders(int[] order)
    {
        var ctx = new MultiTableContext(nodesPerTable: 128);
        var harness = new PersistentTreeHarness(ctx);

        var v1 = harness.BuildCompleteBinaryTree(4); // 31 nodes
        harness.AddRoot(v1[0]);

        var v2 = harness.PathCopy(v1, 15); // leftmost leaf (depth 4)
        harness.AddRoot(v2[0]);

        var v3 = harness.PathCopy(v1, 22); // middle-right leaf
        harness.AddRoot(v3[0]);

        var v4 = harness.PathCopy(v2, 30); // rightmost leaf from V2
        harness.AddRoot(v4[0]);

        var v5 = harness.PathCopy(v3, 7);  // interior node from V3
        harness.AddRoot(v5[0]);

        harness.AddRoot(v1[1]);            // interior root: left subtree of V1

        harness.Verify();

        var roots = new[] { v1[0], v2[0], v3[0], v4[0], v5[0], v1[1] };
        foreach (var releaseOrderIndex in order)
        {
            harness.ReleaseRoot(roots[releaseOrderIndex]);
            harness.Verify();
        }
    }

    [Fact]
    public void Persistent_InterleavedAddAndRelease()
    {
        var ctx = new MultiTableContext(nodesPerTable: 64);
        var harness = new PersistentTreeHarness(ctx);

        // Build original and add root
        var v1 = harness.BuildCompleteBinaryTree(3);
        harness.AddRoot(v1[0]);
        harness.Verify();

        // Create V2, add root
        var v2 = harness.PathCopy(v1, 5);
        harness.AddRoot(v2[0]);
        harness.Verify();

        // Release V1 — V2's shared subtrees survive
        harness.ReleaseRoot(v1[0]);
        harness.Verify();

        // Create V3 from V2 (still alive)
        var v3 = harness.PathCopy(v2, 3);
        harness.AddRoot(v3[0]);
        harness.Verify();

        // Add interior root to V2's right subtree — keeps it alive independently
        harness.AddRoot(v2[2]);
        harness.Verify();

        // Release V2 — interior root keeps right subtree alive
        harness.ReleaseRoot(v2[0]);
        harness.Verify();

        // Create V4 from V3 (still alive)
        var v4 = harness.PathCopy(v3, 6);
        harness.AddRoot(v4[0]);
        harness.Verify();

        // Release V3
        harness.ReleaseRoot(v3[0]);
        harness.Verify();

        // Release interior root
        harness.ReleaseRoot(v2[2]);
        harness.Verify();

        // Release V4 — everything should clean up
        harness.ReleaseRoot(v4[0]);
        harness.Verify();
    }

    // ── Fork-then-release: only the old spine is freed ───────────────────

    [Theory]
    [InlineData(3, 7)]   // depth 3: 15 nodes, leftmost leaf
    [InlineData(3, 12)]  // depth 3: 15 nodes, middle-right leaf
    [InlineData(4, 15)]  // depth 4: 31 nodes, leftmost leaf
    [InlineData(4, 22)]  // depth 4: 31 nodes, middle-right leaf
    [InlineData(5, 31)]  // depth 5: 63 nodes, leftmost leaf
    [InlineData(5, 60)]  // depth 5: 63 nodes, near-rightmost leaf
    public void Fork_ThenReleaseOldRoot_FreesOnlyOldSpine(int depth, int targetLeaf)
    {
        var nodeCount = (1 << (depth + 1)) - 1;
        var ctx = new MultiTableContext(nodesPerTable: nodeCount * 3);
        var harness = new PersistentTreeHarness(ctx);

        var v1 = harness.BuildCompleteBinaryTree(depth);
        harness.AddRoot(v1[0]);
        harness.Verify();

        var v2 = harness.PathCopy(v1, targetLeaf);
        harness.AddRoot(v2[0]);
        harness.Verify();

        var freedBefore = ctx.Freed[0].Count + ctx.Freed[1].Count + ctx.Freed[2].Count;
        Assert.Equal(0, freedBefore);

        harness.ReleaseRoot(v1[0]);
        harness.Verify();

        // The old root was released. Only the old spine (root → target) should be freed.
        // Path length = depth + 1 nodes (root through the target leaf).
        var pathLength = depth + 1;
        var freedAfter = ctx.Freed[0].Count + ctx.Freed[1].Count + ctx.Freed[2].Count;
        Assert.Equal(pathLength, freedAfter);

        // Verify each freed node is one of the old spine positions
        var freedSet = new HashSet<(int TableId, int NodeIndex)>();
        for (var table = 0; table < 3; table++)
            foreach (var idx in ctx.Freed[table])
                freedSet.Add((table, idx));

        var pos = targetLeaf;
        while (pos > 0)
        {
            Assert.Contains(v1[pos], freedSet);
            pos = (pos - 1) / 2;
        }

        Assert.Contains(v1[0], freedSet);

        // All new nodes (V2's spine) should still be alive
        var pathPositions = new List<int>();
        pos = targetLeaf;
        while (pos > 0)
        {
            pathPositions.Add(pos);
            pos = (pos - 1) / 2;
        }

        pathPositions.Add(0);
        foreach (var pathPosition in pathPositions)
            Assert.True(ctx.Tables[v2[pathPosition].TableId].GetCount(new Handle<DummyNode>(v2[pathPosition].NodeIndex)) > 0,
                $"V2 spine node at position {pathPosition} should be alive");

        // Shared subtree nodes (not on the path) should still be alive with refcount 1
        for (var n = 0; n < nodeCount; n++)
        {
            if (pathPositions.Contains(n)) continue;
            var (tableId, nodeIndex) = v1[n]; // same as v2[n] for non-path positions
            Assert.True(ctx.Tables[tableId].GetCount(new Handle<DummyNode>(nodeIndex)) > 0, $"Shared node at position {n} should be alive");
        }

        // Release V2 — now everything should be freed
        harness.ReleaseRoot(v2[0]);
        harness.Verify();

        var totalFreed = ctx.Freed[0].Count + ctx.Freed[1].Count + ctx.Freed[2].Count;
        Assert.Equal(nodeCount + pathLength, totalFreed);
    }

    // ═══════════════════════════ Debug assertions ═════════════════════════

#if DEBUG
    [Fact]
    public void Debug_MutatingFromDifferentThread_TriggersAssert()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(2);
        table.Increment(new Handle<DummyNode>(0));

        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var listener = new AssertThrowsListener();
                table.Increment(new Handle<DummyNode>(1));
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
        table.Increment(new Handle<DummyNode>(0));

        Exception? caught = null;
        try
        {
            using var listener = new AssertThrowsListener();
            table.Decrement(new Handle<DummyNode>(0));
            table.Decrement(new Handle<DummyNode>(0));
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
