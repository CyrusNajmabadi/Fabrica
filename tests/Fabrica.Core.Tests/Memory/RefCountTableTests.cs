using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class RefCountTableTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    private static RefCountTable CreateTinyTable(int directoryLength = 4, int slabShift = 2)
        => new(directoryLength, slabShift);

    private readonly struct FreeTracker : RefCountTable.IRefCountEvents
    {
        private readonly List<int> _freed;

        public FreeTracker()
            => _freed = [];

        public readonly List<int> Freed => _freed;

        public readonly void OnFreed(int index)
            => _freed.Add(index);
    }

    private struct NullEvents : RefCountTable.IRefCountEvents
    {
        public readonly void OnFreed(int index) { }
    }

    private struct NoChildren : RefCountTable.IChildEnumerator
    {
        public readonly void EnumerateChildren(int index, RefCountTable table) { }
    }

    private struct BinaryTreeChildren(int maxIndex) : RefCountTable.IChildEnumerator
    {
        public readonly void EnumerateChildren(int index, RefCountTable table)
        {
            var left = (index * 2) + 1;
            var right = (index * 2) + 2;
            if (left <= maxIndex)
                table.DecrementChild(left);
            if (right <= maxIndex)
                table.DecrementChild(right);
        }
    }

    private struct LinearChainChildren(int maxIndex) : RefCountTable.IChildEnumerator
    {
        public readonly void EnumerateChildren(int index, RefCountTable table)
        {
            var next = index + 1;
            if (next <= maxIndex)
                table.DecrementChild(next);
        }
    }

    private struct WideTreeChildren(int fanout) : RefCountTable.IChildEnumerator
    {
        public readonly void EnumerateChildren(int index, RefCountTable table)
        {
            if (index != 0)
                return;
            for (var i = 1; i <= fanout; i++)
                table.DecrementChild(i);
        }
    }

    private struct SingleChildEnumerator(int parentIndex, int childIndex) : RefCountTable.IChildEnumerator
    {
        public readonly void EnumerateChildren(int index, RefCountTable table)
        {
            if (index == parentIndex)
                table.DecrementChild(childIndex);
        }
    }

    // ═══════════════════════════ EnsureCapacity ═══════════════════════════

    [Fact]
    public void EnsureCapacity_AllocatesRequiredSlabs()
    {
        var table = CreateTinyTable(directoryLength: 4, slabShift: 2); // 4 per slab
        var ta = table.GetTestAccessor();

        for (var i = 0; i < 4; i++)
            Assert.Null(ta.Directory[i]);

        table.EnsureCapacity(5); // needs slabs 0 (indices 0-3) and 1 (index 4)
        Assert.NotNull(ta.Directory[0]);
        Assert.NotNull(ta.Directory[1]);
        Assert.Null(ta.Directory[2]);
    }

    [Fact]
    public void EnsureCapacity_MultipleSlabs()
    {
        var table = CreateTinyTable(directoryLength: 8, slabShift: 2); // 4 per slab
        var ta = table.GetTestAccessor();

        table.EnsureCapacity(12); // needs slabs 0, 1, 2 (indices 0-11)
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
        var tracker = new FreeTracker();
        table.Increment(0);
        table.Increment(0);
        table.Decrement(0, tracker, default(NoChildren));
        Assert.Equal(1, table.GetCount(0));
        Assert.Empty(tracker.Freed);
    }

    [Fact]
    public void Decrement_ToZero_FiresOnFreed()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(1);
        var tracker = new FreeTracker();
        table.Increment(0);
        table.Decrement(0, tracker, default(NoChildren));
        Assert.Equal(0, table.GetCount(0));
        Assert.Single(tracker.Freed);
        Assert.Equal(0, tracker.Freed[0]);
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
        var tracker = new FreeTracker();
        table.Increment(0);

        table.Decrement(0, tracker, new BinaryTreeChildren(0));

        Assert.Equal(0, table.GetCount(0));
        Assert.Single(tracker.Freed);
        Assert.Equal(0, tracker.Freed[0]);
    }

    [Fact]
    public void CascadeDecrement_SmallBinaryTree()
    {
        var table = CreateTinyTable(directoryLength: 4, slabShift: 2);
        table.EnsureCapacity(7);
        var tracker = new FreeTracker();

        for (var i = 0; i <= 6; i++)
            table.Increment(i);

        table.Decrement(0, tracker, new BinaryTreeChildren(6));

        for (var i = 0; i <= 6; i++)
            Assert.Equal(0, table.GetCount(i));

        Assert.Equal(7, tracker.Freed.Count);
        Assert.Contains(0, tracker.Freed);
        Assert.Contains(6, tracker.Freed);
    }

    [Fact]
    public void CascadeDecrement_SharedChild_NotFreedUntilBothParentsRelease()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(3);
        var tracker = new FreeTracker();

        table.Increment(0);
        table.Increment(1);
        table.Increment(1); // shared: two parents
        table.Increment(2);

        table.Decrement(0, tracker, new SingleChildEnumerator(0, 1));

        Assert.Equal(1, table.GetCount(1));
        Assert.Single(tracker.Freed);
        Assert.Equal(0, tracker.Freed[0]);

        tracker.Freed.Clear();
        table.Decrement(2, tracker, new SingleChildEnumerator(2, 1));

        Assert.Equal(0, table.GetCount(1));
        Assert.Equal(2, tracker.Freed.Count);
        Assert.Contains(2, tracker.Freed);
        Assert.Contains(1, tracker.Freed);
    }

    [Fact]
    public void CascadeDecrement_DoesNotFreeWhenRefcountStaysAboveZero()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(1);
        var tracker = new FreeTracker();

        table.Increment(0);
        table.Increment(0); // rc = 2

        table.Decrement(0, tracker, new BinaryTreeChildren(0));

        Assert.Equal(1, table.GetCount(0));
        Assert.Empty(tracker.Freed);
    }

    // ═══════════════════════════ Cascade-free: linear chain ═══════════════

    [Fact]
    public void CascadeDecrement_LinearChain_FreesAll()
    {
        const int ChainLength = 100;
        var table = new RefCountTable(directoryLength: 4, slabShift: 5); // 32 per slab
        table.EnsureCapacity(ChainLength);
        var tracker = new FreeTracker();

        for (var i = 0; i < ChainLength; i++)
            table.Increment(i);

        table.Decrement(0, tracker, new LinearChainChildren(ChainLength - 1));

        Assert.Equal(ChainLength, tracker.Freed.Count);
        for (var i = 0; i < ChainLength; i++)
            Assert.Equal(0, table.GetCount(i));
    }

    [Fact]
    public void CascadeDecrement_DeepChain_NoStackOverflow()
    {
        const int Depth = 10_000;
        var table = new RefCountTable();
        table.EnsureCapacity(Depth);
        var tracker = new FreeTracker();

        for (var i = 0; i < Depth; i++)
            table.Increment(i);

        table.Decrement(0, tracker, new LinearChainChildren(Depth - 1));

        Assert.Equal(Depth, tracker.Freed.Count);
    }

    // ═══════════════════════════ Cascade-free: wide tree ══════════════════

    [Fact]
    public void CascadeDecrement_WideTree_FreesAll()
    {
        const int Fanout = 50;
        var table = CreateTinyTable(directoryLength: 8, slabShift: 3);
        table.EnsureCapacity(Fanout + 1);
        var tracker = new FreeTracker();

        table.Increment(0);
        for (var i = 1; i <= Fanout; i++)
            table.Increment(i);

        table.Decrement(0, tracker, new WideTreeChildren(Fanout));

        Assert.Equal(Fanout + 1, tracker.Freed.Count);
        Assert.Contains(0, tracker.Freed);
        for (var i = 1; i <= Fanout; i++)
        {
            Assert.Equal(0, table.GetCount(i));
            Assert.Contains(i, tracker.Freed);
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
        var tracker = new FreeTracker();

        table.Increment(0);
        table.Increment(0);
        table.Increment(1);
        table.Increment(2);

        int[] batch = [0, 1, 2];
        table.DecrementBatch(batch, tracker, default(NoChildren));

        Assert.Equal(1, table.GetCount(0)); // was 2, now 1
        Assert.Equal(0, table.GetCount(1)); // was 1, now 0 → freed
        Assert.Equal(0, table.GetCount(2)); // was 1, now 0 → freed

        Assert.Equal(2, tracker.Freed.Count);
        Assert.Contains(1, tracker.Freed);
        Assert.Contains(2, tracker.Freed);
    }

    [Fact]
    public void DecrementBatch_CascadesThroughChildren()
    {
        // Nodes 0-6 form a binary tree. Batch-decrement root (0) → should cascade free all 7.
        var table = CreateTinyTable(directoryLength: 4, slabShift: 2);
        table.EnsureCapacity(7);
        var tracker = new FreeTracker();

        for (var i = 0; i <= 6; i++)
            table.Increment(i);

        int[] batch = [0];
        table.DecrementBatch(batch, tracker, new BinaryTreeChildren(6));

        Assert.Equal(7, tracker.Freed.Count);
        for (var i = 0; i <= 6; i++)
            Assert.Equal(0, table.GetCount(i));
    }

    [Fact]
    public void DecrementBatch_Empty_DoesNothing()
    {
        var table = CreateTinyTable();
        var tracker = new FreeTracker();
        table.DecrementBatch([], tracker, default(NoChildren));
        Assert.Empty(tracker.Freed);
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
        var tracker = new FreeTracker();

        table.Increment(0);
        table.Increment(0);
        table.Increment(1);

        table.Decrement(0, tracker, default(NoChildren));
        Assert.Equal(1, table.GetCount(0));
        Assert.Empty(tracker.Freed);

        table.Increment(0);
        Assert.Equal(2, table.GetCount(0));

        table.Decrement(0, tracker, default(NoChildren));
        table.Decrement(0, tracker, default(NoChildren));
        Assert.Equal(0, table.GetCount(0));
        Assert.Single(tracker.Freed);
    }

    [Fact]
    public void ManyIncrementsThenBatchDecrement()
    {
        var table = CreateTinyTable(directoryLength: 8, slabShift: 3);
        const int Count = 20;
        table.EnsureCapacity(Count);
        var tracker = new FreeTracker();

        var indices = new int[Count];
        for (var i = 0; i < Count; i++)
        {
            indices[i] = i;
            table.Increment(i);
        }

        table.DecrementBatch(indices, tracker, default(NoChildren));

        Assert.Equal(Count, tracker.Freed.Count);
        for (var i = 0; i < Count; i++)
            Assert.Equal(0, table.GetCount(i));
    }

    // ═══════════════════════════ Cross-table bounce-back ══════════════════

    private sealed class CrossTableContext
    {
        public RefCountTable TableA { get; set; } = null!;
        public RefCountTable TableB { get; set; } = null!;
        public List<int> FreedA { get; } = [];
        public List<int> FreedB { get; } = [];
    }

    private struct CrossTableAEvents(CrossTableContext ctx) : RefCountTable.IRefCountEvents
    {
        public readonly void OnFreed(int index)
            => ctx.FreedA.Add(index);
    }

    private struct CrossTableBEvents(CrossTableContext ctx) : RefCountTable.IRefCountEvents
    {
        public readonly void OnFreed(int index)
            => ctx.FreedB.Add(index);
    }

    /// <summary>A-node 0 has a cross-reference to B-node 0.</summary>
    private struct CrossTableAChildren(CrossTableContext ctx) : RefCountTable.IChildEnumerator
    {
        public readonly void EnumerateChildren(int index, RefCountTable table)
        {
            if (index == 0)
                ctx.TableB.Decrement(0, new CrossTableBEvents(ctx), new CrossTableBChildren(ctx));
        }
    }

    /// <summary>B-node 0 has a cross-reference to A-node 1.</summary>
    private struct CrossTableBChildren(CrossTableContext ctx) : RefCountTable.IChildEnumerator
    {
        public readonly void EnumerateChildren(int index, RefCountTable table)
        {
            if (index == 0)
                ctx.TableA.Decrement(1, new CrossTableAEvents(ctx), new CrossTableAChildren(ctx));
        }
    }

    [Fact]
    public void CrossTable_BounceBack_CascadesCorrectly()
    {
        var ctx = new CrossTableContext
        {
            TableA = CreateTinyTable(directoryLength: 4, slabShift: 2),
            TableB = CreateTinyTable(directoryLength: 4, slabShift: 2),
        };
        ctx.TableA.EnsureCapacity(2);
        ctx.TableB.EnsureCapacity(1);

        ctx.TableA.Increment(0); // A[0] = 1
        ctx.TableA.Increment(1); // A[1] = 1
        ctx.TableB.Increment(0); // B[0] = 1

        // Cascade from A[0]: frees A[0] → cascades to B[0] → frees B[0] → cascades to A[1] → frees A[1]
        ctx.TableA.Decrement(0, new CrossTableAEvents(ctx), new CrossTableAChildren(ctx));

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
    public void CrossTable_BounceBack_SharedChildSurvives()
    {
        // A[0] → B[0], B[0] → A[1]. A[1] has rc=2 (shared by another parent).
        // Cascade from A[0] should free A[0] and B[0], but NOT A[1].
        var ctx = new CrossTableContext
        {
            TableA = CreateTinyTable(directoryLength: 4, slabShift: 2),
            TableB = CreateTinyTable(directoryLength: 4, slabShift: 2),
        };
        ctx.TableA.EnsureCapacity(2);
        ctx.TableB.EnsureCapacity(1);

        ctx.TableA.Increment(0); // A[0] = 1
        ctx.TableA.Increment(1); // A[1] = 1
        ctx.TableA.Increment(1); // A[1] = 2 (shared)
        ctx.TableB.Increment(0); // B[0] = 1

        ctx.TableA.Decrement(0, new CrossTableAEvents(ctx), new CrossTableAChildren(ctx));

        Assert.Equal(0, ctx.TableA.GetCount(0)); // freed
        Assert.Equal(1, ctx.TableA.GetCount(1)); // survived (rc was 2, now 1)
        Assert.Equal(0, ctx.TableB.GetCount(0)); // freed

        Assert.Single(ctx.FreedA); // only A[0]
        Assert.Equal(0, ctx.FreedA[0]);
        Assert.Single(ctx.FreedB); // only B[0]
        Assert.Equal(0, ctx.FreedB[0]);
    }

    // ═══════════════════════════ Re-entrancy flag state ═══════════════════

    [Fact]
    public void CascadeInProgress_FalseBeforeAndAfterDecrement()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(3);
        var ta = table.GetTestAccessor();

        table.Increment(0);
        table.Increment(1);
        table.Increment(2);

        Assert.False(ta.CascadeInProgress);
        table.Decrement(0, default(NullEvents), new LinearChainChildren(2));
        Assert.False(ta.CascadeInProgress);
    }

    // ═══════════════════════════ Debug assertions ═════════════════════════

#if DEBUG
    [Fact]
    public void Debug_MutatingFromDifferentThread_TriggersAssert()
    {
        var table = CreateTinyTable();
        table.EnsureCapacity(2);
        table.Increment(0); // establish owner

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
            table.Decrement(0, default(NullEvents), default(NoChildren)); // rc = 0
            table.Decrement(0, default(NullEvents), default(NoChildren)); // rc would go to -1 → assert
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
