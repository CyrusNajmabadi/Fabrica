using Fabrica.Core.Threading.Queues;
using Xunit;

namespace Fabrica.Core.Tests.Collections;

public class ProducerConsumerQueueTests
{
    // ═══════════════════════════ TEST HELPERS ════════════════════════════════

    private readonly struct TrackingCleanupHandler : ProducerConsumerQueue<string>.ICleanupHandler
    {
        public List<(long Position, string? Item)> CleanedEntries { get; }

        public TrackingCleanupHandler()
            => this.CleanedEntries = [];

        public readonly void HandleCleanup(long position, in string item)
            => this.CleanedEntries.Add((position, item));
    }

    // ═══════════════════════════ APPEND + ACQUIRE ════════════════════════════

    [Fact]
    public void ConsumerAcquire_ReturnsEmpty_WhenNothingProduced()
    {
        var queue = new ProducerConsumerQueue<string>();
        var segment = queue.ConsumerAcquire();
        Assert.True(segment.IsEmpty);
        Assert.Equal(0, segment.Count);
    }

    [Fact]
    public void ProducerAppend_SingleItem_ConsumerAcquiresIt()
    {
        var queue = new ProducerConsumerQueue<string>();
        queue.ProducerAppend("tick-0");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(1, segment.Count);
        Assert.Equal("tick-0", segment[0]);
    }

    [Fact]
    public void ProducerAppend_MultipleItems_ConsumerAcquiresAll()
    {
        var queue = new ProducerConsumerQueue<string>();
        for (var i = 0; i < 5; i++)
            queue.ProducerAppend($"tick-{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(5, segment.Count);

        for (var i = 0; i < 5; i++)
            Assert.Equal($"tick-{i}", segment[i]);
    }

    [Fact]
    public void ConsumerAcquire_ReturnsEmpty_AfterConsumerCaughtUp()
    {
        var queue = new ProducerConsumerQueue<string>();
        queue.ProducerAppend("tick-0");

        var segment = queue.ConsumerAcquire();
        queue.ConsumerAdvance(segment.Count);

        var second = queue.ConsumerAcquire();
        Assert.True(second.IsEmpty);
    }

    // ═══════════════════════════ ACQUIRE / ADVANCE CYCLE ════════════════════

    [Fact]
    public void MultipleAcquireAdvanceCycles_ReturnsOnlyNewItems()
    {
        var queue = new ProducerConsumerQueue<string>();

        queue.ProducerAppend("a");
        queue.ProducerAppend("b");
        var seg1 = queue.ConsumerAcquire();
        Assert.Equal(2, seg1.Count);
        queue.ConsumerAdvance(seg1.Count);

        queue.ProducerAppend("c");
        var seg2 = queue.ConsumerAcquire();
        Assert.Equal(1, seg2.Count);
        Assert.Equal("c", seg2[0]);
        queue.ConsumerAdvance(seg2.Count);
    }

    [Fact]
    public void ConsumerAdvance_WithEmptySegment_IsNoOp()
    {
        var queue = new ProducerConsumerQueue<string>();
        var empty = queue.ConsumerAcquire();
        queue.ConsumerAdvance(empty.Count);

        queue.ProducerAppend("a");
        var segment = queue.ConsumerAcquire();
        Assert.Equal(1, segment.Count);
        Assert.Equal("a", segment[0]);
    }

    [Fact]
    public void ProducerPosition_AdvancesWithEachAppend()
    {
        var queue = new ProducerConsumerQueue<string>();
        var accessor = queue.GetTestAccessor();

        Assert.Equal(0, accessor.ProducerPosition);
        queue.ProducerAppend("a");
        Assert.Equal(1, accessor.ProducerPosition);
        queue.ProducerAppend("b");
        Assert.Equal(2, accessor.ProducerPosition);
    }

    [Fact]
    public void ConsumerPosition_AdvancesAfterConsumerAdvance()
    {
        var queue = new ProducerConsumerQueue<string>();
        var accessor = queue.GetTestAccessor();

        queue.ProducerAppend("a");
        queue.ProducerAppend("b");

        Assert.Equal(0, accessor.ConsumerPosition);
        var segment = queue.ConsumerAcquire();
        queue.ConsumerAdvance(segment.Count);
        Assert.Equal(2, accessor.ConsumerPosition);
    }

    // ═══════════════════════════ SLAB BOUNDARY ══════════════════════════════

    [Fact]
    public void AppendAcross_SlabBoundary_CreatesNewSlab()
    {
        var queue = new ProducerConsumerQueue<string>();
        var accessor = queue.GetTestAccessor();
        var slabLength = ProducerConsumerQueue<string>.SlabSizeHelper.SlabLength;

        for (var i = 0; i < slabLength + 1; i++)
            queue.ProducerAppend($"tick-{i}");

        Assert.NotSame(accessor.HeadSlab, accessor.TailSlab);
        Assert.Equal(slabLength + 1, accessor.ProducerPosition);
    }

    [Fact]
    public void ConsumerAcquiresItems_AcrossSlabBoundary()
    {
        var queue = new ProducerConsumerQueue<string>();
        var slabLength = ProducerConsumerQueue<string>.SlabSizeHelper.SlabLength;
        var totalItems = slabLength + 10;

        for (var i = 0; i < totalItems; i++)
            queue.ProducerAppend($"tick-{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(totalItems, segment.Count);

        for (var i = 0; i < totalItems; i++)
            Assert.Equal($"tick-{i}", segment[i]);
    }

    [Fact]
    public void Enumerator_CrossesSlabBoundary()
    {
        var queue = new ProducerConsumerQueue<string>();
        var slabLength = ProducerConsumerQueue<string>.SlabSizeHelper.SlabLength;
        var totalItems = slabLength + 10;

        for (var i = 0; i < totalItems; i++)
            queue.ProducerAppend($"tick-{i}");

        var segment = queue.ConsumerAcquire();
        var collected = new List<string>();
        foreach (ref readonly var item in segment)
            collected.Add(item);

        Assert.Equal(totalItems, collected.Count);
        for (var i = 0; i < totalItems; i++)
            Assert.Equal($"tick-{i}", collected[i]);
    }

    [Fact]
    public void ConsumerAdvance_AtExactSlabBoundary_ThenAcquireMore()
    {
        var queue = new ProducerConsumerQueue<string>();
        var slabLength = ProducerConsumerQueue<string>.SlabSizeHelper.SlabLength;

        for (var i = 0; i < slabLength; i++)
            queue.ProducerAppend($"tick-{i}");

        var seg1 = queue.ConsumerAcquire();
        Assert.Equal(slabLength, seg1.Count);
        queue.ConsumerAdvance(seg1.Count);

        for (var i = slabLength; i < slabLength + 5; i++)
            queue.ProducerAppend($"tick-{i}");

        var seg2 = queue.ConsumerAcquire();
        Assert.Equal(5, seg2.Count);
        Assert.Equal($"tick-{slabLength}", seg2[0]);
        Assert.Equal($"tick-{slabLength + 4}", seg2[4]);
    }

    // ═══════════════════════════ CLEANUP ═════════════════════════════════════

    [Fact]
    public void ProducerCleanup_CallsHandlerForEachItem()
    {
        var queue = new ProducerConsumerQueue<string>();
        queue.ProducerAppend("a");
        queue.ProducerAppend("b");

        var segment = queue.ConsumerAcquire();
        queue.ConsumerAdvance(segment.Count);

        var handler = new TrackingCleanupHandler();
        queue.ProducerCleanup(ref handler);

        Assert.Equal(2, handler.CleanedEntries.Count);
        Assert.Equal((0, "a"), handler.CleanedEntries[0]);
        Assert.Equal((1, "b"), handler.CleanedEntries[1]);
    }

    [Fact]
    public void ProducerCleanup_ClearsSlots()
    {
        var queue = new ProducerConsumerQueue<string>();
        var accessor = queue.GetTestAccessor();
        queue.ProducerAppend("a");

        var segment = queue.ConsumerAcquire();
        queue.ConsumerAdvance(segment.Count);

        var handler = new TrackingCleanupHandler();
        queue.ProducerCleanup(ref handler);

        Assert.Null(accessor.HeadSlab.Entries[0]);
    }

    [Fact]
    public void ProducerCleanup_AdvancesCleanupPosition()
    {
        var queue = new ProducerConsumerQueue<string>();
        var accessor = queue.GetTestAccessor();

        queue.ProducerAppend("a");
        queue.ProducerAppend("b");

        var segment = queue.ConsumerAcquire();
        queue.ConsumerAdvance(segment.Count);

        Assert.Equal(0, accessor.CleanupPosition);
        var handler = new TrackingCleanupHandler();
        queue.ProducerCleanup(ref handler);
        Assert.Equal(2, accessor.CleanupPosition);
    }

    [Fact]
    public void ProducerCleanup_DoesNotCleanBeyondConsumerPosition()
    {
        var queue = new ProducerConsumerQueue<string>();
        queue.ProducerAppend("a");
        queue.ProducerAppend("b");

        var segment = queue.ConsumerAcquire();
        queue.ConsumerAdvance(segment.Count);

        queue.ProducerAppend("c");

        var handler = new TrackingCleanupHandler();
        queue.ProducerCleanup(ref handler);

        Assert.Equal(2, handler.CleanedEntries.Count);
    }

    [Fact]
    public void ProducerCleanup_RecyclesFullSlabs()
    {
        var queue = new ProducerConsumerQueue<string>();
        var accessor = queue.GetTestAccessor();
        var slabLength = ProducerConsumerQueue<string>.SlabSizeHelper.SlabLength;

        for (var i = 0; i < slabLength + 1; i++)
            queue.ProducerAppend($"tick-{i}");

        var segment = queue.ConsumerAcquire();
        queue.ConsumerAdvance(segment.Count);

        Assert.False(accessor.HasFreeSlabs);
        var handler = new TrackingCleanupHandler();
        queue.ProducerCleanup(ref handler);

        Assert.True(accessor.HasFreeSlabs);
        Assert.Equal(slabLength + 1, handler.CleanedEntries.Count);
    }

    [Fact]
    public void RecycledSlab_IsReusedForNewAppends()
    {
        var queue = new ProducerConsumerQueue<string>();
        var accessor = queue.GetTestAccessor();
        var slabLength = ProducerConsumerQueue<string>.SlabSizeHelper.SlabLength;

        for (var i = 0; i < slabLength + 1; i++)
            queue.ProducerAppend($"round1-{i}");

        var segment = queue.ConsumerAcquire();
        queue.ConsumerAdvance(segment.Count);

        var handler = new TrackingCleanupHandler();
        queue.ProducerCleanup(ref handler);
        Assert.True(accessor.HasFreeSlabs);

        for (var i = 0; i < slabLength; i++)
            queue.ProducerAppend($"round2-{i}");

        Assert.False(accessor.HasFreeSlabs);

        var seg2 = queue.ConsumerAcquire();
        Assert.Equal(slabLength, seg2.Count);
        Assert.Equal("round2-0", seg2[0]);
    }

    // ═══════════════════════════ SEGMENT INDEXER ═════════════════════════════

    [Fact]
    public void Segment_Indexer_ThrowsOnNegativeIndex()
    {
        var queue = new ProducerConsumerQueue<string>();
        queue.ProducerAppend("a");
        var segment = queue.ConsumerAcquire();

        try
        {
            _ = segment[-1];
            Assert.Fail("Expected ArgumentOutOfRangeException");
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    [Fact]
    public void Segment_Indexer_ThrowsOnOutOfBoundsIndex()
    {
        var queue = new ProducerConsumerQueue<string>();
        queue.ProducerAppend("a");
        var segment = queue.ConsumerAcquire();

        try
        {
            _ = segment[1];
            Assert.Fail("Expected ArgumentOutOfRangeException");
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    [Fact]
    public void Segment_Indexer_RandomAccess_AcrossSlabBoundary()
    {
        var queue = new ProducerConsumerQueue<string>();
        var slabLength = ProducerConsumerQueue<string>.SlabSizeHelper.SlabLength;

        for (var i = 0; i < slabLength + 5; i++)
            queue.ProducerAppend($"tick-{i}");

        var segment = queue.ConsumerAcquire();

        Assert.Equal("tick-0", segment[0]);
        Assert.Equal($"tick-{slabLength - 1}", segment[slabLength - 1]);
        Assert.Equal($"tick-{slabLength}", segment[slabLength]);
        Assert.Equal($"tick-{slabLength + 4}", segment[slabLength + 4]);
    }

    // ═══════════════════════════ LARGE VOLUME ════════════════════════════════

    [Fact]
    public void LargeVolume_ManySlabs_NoDataLoss()
    {
        var queue = new ProducerConsumerQueue<string>();
        var slabLength = ProducerConsumerQueue<string>.SlabSizeHelper.SlabLength;
        var totalItems = (slabLength * 3) + 42;

        for (var i = 0; i < totalItems; i++)
            queue.ProducerAppend($"tick-{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(totalItems, segment.Count);

        var index = 0;
        foreach (ref readonly var item in segment)
        {
            Assert.Equal($"tick-{index}", item);
            index++;
        }

        Assert.Equal(totalItems, index);
    }

    [Fact]
    public void IncrementalProduceConsumeCleanup_OverMultipleSlabs()
    {
        var queue = new ProducerConsumerQueue<string>();
        var accessor = queue.GetTestAccessor();
        var slabLength = ProducerConsumerQueue<string>.SlabSizeHelper.SlabLength;
        var handler = new TrackingCleanupHandler();
        var totalCleaned = 0;

        for (var batch = 0; batch < 5; batch++)
        {
            var batchSize = (slabLength / 2) + 7;
            for (var i = 0; i < batchSize; i++)
                queue.ProducerAppend($"batch{batch}-{i}");

            var segment = queue.ConsumerAcquire();
            Assert.Equal(batchSize, segment.Count);
            Assert.Equal($"batch{batch}-0", segment[0]);
            queue.ConsumerAdvance(segment.Count);

            queue.ProducerCleanup(ref handler);
            totalCleaned += batchSize;
            Assert.Equal(totalCleaned, handler.CleanedEntries.Count);
        }

        Assert.Equal(accessor.ProducerPosition, accessor.CleanupPosition);
    }
}
