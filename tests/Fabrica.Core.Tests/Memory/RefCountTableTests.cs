using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class RefCountTableTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>Creates a table with tiny parameters for edge-case testing. slabShift=2 → 4 entries per slab.</summary>
    private static RefCountTable CreateTinyTable(int directoryLength = 4, int slabShift = 2)
        => new(directoryLength, slabShift);

    /// <summary>Records freed indices for assertions.</summary>
    private sealed class FreeTracker : RefCountTable.IRefCountEvents
    {
        public List<int> Freed { get; } = [];

        public void OnFreed(int index)
            => this.Freed.Add(index);
    }

    /// <summary>No-op events — nothing happens on free.</summary>
    private sealed class NullEvents : RefCountTable.IRefCountEvents
    {
        public static readonly NullEvents Instance = new();

        public void OnFreed(int index) { }
    }

    /// <summary>Binary tree child enumerator for cascade tests. Left = index*2+1, Right = index*2+2.</summary>
    private sealed class BinaryTreeChildren(int maxIndex) : RefCountTable.IChildEnumerator
    {
        public void EnumerateChildren(int index, ref UnsafeStack<int> worklist, RefCountTable table)
        {
            var left = (index * 2) + 1;
            var right = (index * 2) + 2;
            if (left <= maxIndex)
                table.DecrementChild(left, ref worklist);
            if (right <= maxIndex)
                table.DecrementChild(right, ref worklist);
        }
    }

    /// <summary>Linear chain child enumerator: each node points to index+1.</summary>
    private sealed class LinearChainChildren(int maxIndex) : RefCountTable.IChildEnumerator
    {
        public void EnumerateChildren(int index, ref UnsafeStack<int> worklist, RefCountTable table)
        {
            var next = index + 1;
            if (next <= maxIndex)
                table.DecrementChild(next, ref worklist);
        }
    }

    /// <summary>Wide tree child enumerator: node 0 points to 1..fanout.</summary>
    private sealed class WideTreeChildren(int fanout) : RefCountTable.IChildEnumerator
    {
        public void EnumerateChildren(int index, ref UnsafeStack<int> worklist, RefCountTable table)
        {
            if (index != 0)
                return;
            for (var i = 1; i <= fanout; i++)
                table.DecrementChild(i, ref worklist);
        }
    }

    // ═══════════════════════════ Basic increment/decrement ════════════════

    [Fact]
    public void Increment_SetsCountToOne()
    {
        var table = CreateTinyTable();
        table.Increment(0);
        Assert.Equal(1, table.GetCount(0));
    }

    [Fact]
    public void Increment_Twice_SetsCountToTwo()
    {
        var table = CreateTinyTable();
        table.Increment(0);
        table.Increment(0);
        Assert.Equal(2, table.GetCount(0));
    }

    [Fact]
    public void Decrement_FromTwo_SetsCountToOne_NoFree()
    {
        var table = CreateTinyTable();
        var tracker = new FreeTracker();
        table.Increment(0);
        table.Increment(0);
        table.Decrement(0, tracker);
        Assert.Equal(1, table.GetCount(0));
        Assert.Empty(tracker.Freed);
    }

    [Fact]
    public void Decrement_ToZero_FiresOnFreed()
    {
        var table = CreateTinyTable();
        var tracker = new FreeTracker();
        table.Increment(0);
        table.Decrement(0, tracker);
        Assert.Equal(0, table.GetCount(0));
        Assert.Single(tracker.Freed);
        Assert.Equal(0, tracker.Freed[0]);
    }

    [Fact]
    public void GetCount_Uninitialized_ReturnsZero()
    {
        var table = CreateTinyTable();
        table.Increment(0); // ensure slab 0 is allocated
        Assert.Equal(0, table.GetCount(1));
    }

    [Fact]
    public void MultipleIndices_Independent()
    {
        var table = CreateTinyTable();
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
        var tracker = new FreeTracker();
        table.Increment(0);

        table.DecrementCascade(0, tracker, new BinaryTreeChildren(0));

        Assert.Equal(0, table.GetCount(0));
        Assert.Single(tracker.Freed);
        Assert.Equal(0, tracker.Freed[0]);
    }

    [Fact]
    public void CascadeDecrement_SmallBinaryTree()
    {
        // Tree: 0 -> (1, 2), 1 -> (3, 4), 2 -> (5, 6)
        // All refcounts = 1, cascade from root should free all 7 nodes
        var table = CreateTinyTable(directoryLength: 4, slabShift: 2);
        var tracker = new FreeTracker();

        for (var i = 0; i <= 6; i++)
            table.Increment(i);

        table.DecrementCascade(0, tracker, new BinaryTreeChildren(6));

        for (var i = 0; i <= 6; i++)
            Assert.Equal(0, table.GetCount(i));

        Assert.Equal(7, tracker.Freed.Count);
        Assert.Contains(0, tracker.Freed);
        Assert.Contains(6, tracker.Freed);
    }

    [Fact]
    public void CascadeDecrement_SharedChild_NotFreedUntilBothParentsRelease()
    {
        // Node 1 is a child of both node 0 and node 2 (shared).
        // rc[1] = 2. Releasing node 0 decrements rc[1] to 1 (not freed).
        // Then releasing node 2 decrements rc[1] to 0 (freed).
        var table = CreateTinyTable();
        var tracker = new FreeTracker();

        table.Increment(0);
        table.Increment(1);
        table.Increment(1); // shared: two parents
        table.Increment(2);

        // Node 0's children: just node 1
        table.DecrementCascade(0, tracker, new SingleChildEnumerator(0, 1));

        Assert.Equal(1, table.GetCount(1)); // still alive
        Assert.Single(tracker.Freed); // only node 0 freed
        Assert.Equal(0, tracker.Freed[0]);

        // Node 2's children: also node 1
        tracker.Freed.Clear();
        table.DecrementCascade(2, tracker, new SingleChildEnumerator(2, 1));

        Assert.Equal(0, table.GetCount(1)); // now freed
        Assert.Equal(2, tracker.Freed.Count); // node 2 and node 1
        Assert.Contains(2, tracker.Freed);
        Assert.Contains(1, tracker.Freed);
    }

    [Fact]
    public void CascadeDecrement_DoesNotFreeWhenRefcountStaysAboveZero()
    {
        var table = CreateTinyTable();
        var tracker = new FreeTracker();

        table.Increment(0);
        table.Increment(0); // rc = 2

        table.DecrementCascade(0, tracker, new BinaryTreeChildren(0));

        Assert.Equal(1, table.GetCount(0));
        Assert.Empty(tracker.Freed);
    }

    // ═══════════════════════════ Cascade-free: linear chain ═══════════════

    [Fact]
    public void CascadeDecrement_LinearChain_FreesAll()
    {
        const int ChainLength = 100;
        var table = new RefCountTable(directoryLength: 4, slabShift: 5); // 32 per slab
        var tracker = new FreeTracker();

        for (var i = 0; i < ChainLength; i++)
            table.Increment(i);

        table.DecrementCascade(0, tracker, new LinearChainChildren(ChainLength - 1));

        Assert.Equal(ChainLength, tracker.Freed.Count);
        for (var i = 0; i < ChainLength; i++)
            Assert.Equal(0, table.GetCount(i));
    }

    [Fact]
    public void CascadeDecrement_DeepChain_NoStackOverflow()
    {
        const int Depth = 10_000;
        var table = new RefCountTable(); // production sizing handles 10K easily
        var tracker = new FreeTracker();

        for (var i = 0; i < Depth; i++)
            table.Increment(i);

        table.DecrementCascade(0, tracker, new LinearChainChildren(Depth - 1));

        Assert.Equal(Depth, tracker.Freed.Count);
    }

    // ═══════════════════════════ Cascade-free: wide tree ══════════════════

    [Fact]
    public void CascadeDecrement_WideTree_FreesAll()
    {
        const int Fanout = 50;
        var table = CreateTinyTable(directoryLength: 8, slabShift: 3); // 8 per slab
        var tracker = new FreeTracker();

        table.Increment(0); // root
        for (var i = 1; i <= Fanout; i++)
            table.Increment(i);

        table.DecrementCascade(0, tracker, new WideTreeChildren(Fanout));

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
        int[] indices = [0, 1, 2, 0, 1, 0];
        table.IncrementBatch(indices);

        Assert.Equal(3, table.GetCount(0));
        Assert.Equal(2, table.GetCount(1));
        Assert.Equal(1, table.GetCount(2));
    }

    [Fact]
    public void DecrementBatch_DecrementsAll_FreesAtZero()
    {
        var table = CreateTinyTable();
        var tracker = new FreeTracker();

        table.Increment(0);
        table.Increment(0);
        table.Increment(1);
        table.Increment(2);

        int[] batch = [0, 1, 2];
        table.DecrementBatch(batch, tracker);

        Assert.Equal(1, table.GetCount(0)); // was 2, now 1
        Assert.Equal(0, table.GetCount(1)); // was 1, now 0 → freed
        Assert.Equal(0, table.GetCount(2)); // was 1, now 0 → freed

        Assert.Equal(2, tracker.Freed.Count);
        Assert.Contains(1, tracker.Freed);
        Assert.Contains(2, tracker.Freed);
    }

    [Fact]
    public void DecrementBatch_Empty_DoesNothing()
    {
        var table = CreateTinyTable();
        var tracker = new FreeTracker();
        table.DecrementBatch([], tracker);
        Assert.Empty(tracker.Freed);
    }

    // ═══════════════════════════ Slab boundaries ═════════════════════════

    [Fact]
    public void Increment_CrossesSlabBoundary()
    {
        var table = CreateTinyTable(directoryLength: 4, slabShift: 2); // 4 per slab
        var ta = table.GetTestAccessor();

        table.Increment(3); // last entry of slab 0
        table.Increment(4); // first entry of slab 1

        Assert.NotNull(ta.Directory[0]);
        Assert.NotNull(ta.Directory[1]);
        Assert.Equal(1, table.GetCount(3));
        Assert.Equal(1, table.GetCount(4));
    }

    [Fact]
    public void SlabsAllocatedOnDemand()
    {
        var table = CreateTinyTable(directoryLength: 4, slabShift: 2);
        var ta = table.GetTestAccessor();

        // No operations yet — all slabs null
        for (var i = 0; i < 4; i++)
            Assert.Null(ta.Directory[i]);

        table.Increment(0);
        Assert.NotNull(ta.Directory[0]);
        Assert.Null(ta.Directory[1]);
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
        var tracker = new FreeTracker();

        table.Increment(0);
        table.Increment(0);
        table.Increment(1);

        table.Decrement(0, tracker);
        Assert.Equal(1, table.GetCount(0));
        Assert.Empty(tracker.Freed);

        table.Increment(0);
        Assert.Equal(2, table.GetCount(0));

        table.Decrement(0, tracker);
        table.Decrement(0, tracker);
        Assert.Equal(0, table.GetCount(0));
        Assert.Single(tracker.Freed);
    }

    [Fact]
    public void ManyIncrementsThenBatchDecrement()
    {
        var table = CreateTinyTable(directoryLength: 8, slabShift: 3);
        var tracker = new FreeTracker();
        const int Count = 20;

        var indices = new int[Count];
        for (var i = 0; i < Count; i++)
        {
            indices[i] = i;
            table.Increment(i);
        }

        table.DecrementBatch(indices, tracker);

        Assert.Equal(Count, tracker.Freed.Count);
        for (var i = 0; i < Count; i++)
            Assert.Equal(0, table.GetCount(i));
    }

    // ═══════════════════════════ Debug assertions ═════════════════════════

#if DEBUG
    [Fact]
    public void Debug_MutatingFromDifferentThread_TriggersAssert()
    {
        var table = CreateTinyTable();
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
        Assert.Contains("Mutating operations are single-threaded", caught.Message);
    }

    [Fact]
    public void Debug_DecrementBelowZero_TriggersAssert()
    {
        var table = CreateTinyTable();
        table.Increment(0); // rc = 1, also ensures slab exists

        Exception? caught = null;
        try
        {
            using var listener = new AssertThrowsListener();
            table.Decrement(0, NullEvents.Instance); // rc = 0
            table.Decrement(0, NullEvents.Instance); // rc would go to -1 → assert
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        Assert.Contains("refcount already at", caught.Message);
    }

    /// <summary>Replaces the default trace listener so Debug.Assert failures throw instead of popping a dialog.</summary>
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

    // ═══════════════════════════ Helper child enumerator ══════════════════

    /// <summary>A parent node has exactly one child.</summary>
    private sealed class SingleChildEnumerator(int parentIndex, int childIndex) : RefCountTable.IChildEnumerator
    {
        public void EnumerateChildren(int index, ref UnsafeStack<int> worklist, RefCountTable table)
        {
            if (index == parentIndex)
                table.DecrementChild(childIndex, ref worklist);
        }
    }
}
