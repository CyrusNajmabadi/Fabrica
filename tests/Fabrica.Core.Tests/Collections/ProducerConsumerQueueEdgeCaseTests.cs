using Fabrica.Core.Threading.Queues;
using Xunit;

namespace Fabrica.Core.Tests.Collections;

/// <summary>
/// Edge case tests for <see cref="ProducerConsumerQueue{T}"/> using small slab lengths (typically 4) to make multi-slab scenarios
/// easy to exercise without producing thousands of items. These tests complement <see cref="ProducerConsumerQueueTests"/> which
/// uses the default LOH-aware slab length.
/// </summary>
public class ProducerConsumerQueueEdgeCaseTests
{
    private const int SmallSlabLength = 4;

    private readonly struct TrackingCleanupHandler : ProducerConsumerQueue<string>.ICleanupHandler
    {
        public List<(long Position, string? Item)> CleanedEntries { get; }

        public TrackingCleanupHandler()
            => this.CleanedEntries = [];

        public readonly void HandleCleanup(long position, in string item)
            => this.CleanedEntries.Add((position, item));
    }

    // ═══════════════════════════ SLAB LENGTH CONFIG ══════════════════════════

    [Fact]
    public void InternalConstructor_UsesProvidedSlabLength()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var accessor = queue.GetTestAccessor();
        Assert.Equal(SmallSlabLength, accessor.SlabLength);
    }

    // ═══════════════════════════ CONSUMER READS WITHIN SINGLE SLAB ═══════════

    [Fact]
    public void ConsumerReadsPartialFirstSlab()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        queue.ProducerAppend("a");
        queue.ProducerAppend("b");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(2, segment.Count);
        Assert.Equal("a", segment[0]);
        Assert.Equal("b", segment[1]);
    }

    [Fact]
    public void ConsumerReadsExactlyOneFullSlab()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        for (var i = 0; i < SmallSlabLength; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(SmallSlabLength, segment.Count);
        for (var i = 0; i < SmallSlabLength; i++)
            Assert.Equal($"e{i}", segment[i]);
    }

    // ═══════════════════════════ CONSUMER READS OVERLAPPING INTO SECOND SLAB ═

    [Fact]
    public void ConsumerReadsOverlappingIntoSecondSlab()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        for (var i = 0; i < SmallSlabLength + 2; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(SmallSlabLength + 2, segment.Count);
        for (var i = 0; i < SmallSlabLength + 2; i++)
            Assert.Equal($"e{i}", segment[i]);
    }

    [Fact]
    public void ConsumerReadsExactlyTwoFullSlabs()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var total = SmallSlabLength * 2;

        for (var i = 0; i < total; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(total, segment.Count);
        for (var i = 0; i < total; i++)
            Assert.Equal($"e{i}", segment[i]);
    }

    // ═══════════════════════════ CONSUMER READS SPANNING 3+ SLABS ════════════

    [Fact]
    public void ConsumerReadsSpanningThreeSlabs()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var total = (SmallSlabLength * 2) + 2;

        for (var i = 0; i < total; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(total, segment.Count);
        for (var i = 0; i < total; i++)
            Assert.Equal($"e{i}", segment[i]);
    }

    [Fact]
    public void ConsumerReadsSpanningFiveSlabs()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var total = (SmallSlabLength * 4) + 3;

        for (var i = 0; i < total; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(total, segment.Count);

        var index = 0;
        foreach (ref readonly var item in segment)
        {
            Assert.Equal($"e{index}", item);
            index++;
        }

        Assert.Equal(total, index);
    }

    // ═══════════════════════════ SEGMENT ACROSS MANY SLABS ═══════════════════

    [Fact]
    public void Segment_Indexer_FirstEntryOfEachSlab()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var total = SmallSlabLength * 3;

        for (var i = 0; i < total; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal($"e0", segment[0]);
        Assert.Equal($"e{SmallSlabLength}", segment[SmallSlabLength]);
        Assert.Equal($"e{SmallSlabLength * 2}", segment[SmallSlabLength * 2]);
    }

    [Fact]
    public void Segment_Indexer_LastEntryOfEachSlab()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var total = SmallSlabLength * 3;

        for (var i = 0; i < total; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal($"e{SmallSlabLength - 1}", segment[SmallSlabLength - 1]);
        Assert.Equal($"e{(SmallSlabLength * 2) - 1}", segment[(SmallSlabLength * 2) - 1]);
        Assert.Equal($"e{(SmallSlabLength * 3) - 1}", segment[(SmallSlabLength * 3) - 1]);
    }

    [Fact]
    public void Segment_Enumerator_AcrossThreeSlabs()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var total = (SmallSlabLength * 3) + 1;

        for (var i = 0; i < total; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        var collected = new List<string>();
        foreach (ref readonly var item in segment)
            collected.Add(item);

        Assert.Equal(total, collected.Count);
        for (var i = 0; i < total; i++)
            Assert.Equal($"e{i}", collected[i]);
    }

    // ═══════════════════════════ PARTIAL CONSUME THEN ACQUIRE ACROSS SLABS ═══

    [Fact]
    public void ConsumerAdvancesPartialSlab_ThenAcquiresAcrossSlabBoundary()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        for (var i = 0; i < 2; i++)
            queue.ProducerAppend($"batch1-{i}");

        var seg1 = queue.ConsumerAcquire();
        Assert.Equal(2, seg1.Count);
        queue.ConsumerAdvance(seg1.Count);

        for (var i = 0; i < SmallSlabLength; i++)
            queue.ProducerAppend($"batch2-{i}");

        var seg2 = queue.ConsumerAcquire();
        Assert.Equal(SmallSlabLength, seg2.Count);
        Assert.Equal("batch2-0", seg2[0]);
        Assert.Equal($"batch2-{SmallSlabLength - 1}", seg2[SmallSlabLength - 1]);
    }

    [Fact]
    public void ConsumerAdvancesAtSlabBoundary_NextAcquireStartsOnNewSlab()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        for (var i = 0; i < SmallSlabLength; i++)
            queue.ProducerAppend($"first-{i}");

        var seg1 = queue.ConsumerAcquire();
        queue.ConsumerAdvance(seg1.Count);

        for (var i = 0; i < 3; i++)
            queue.ProducerAppend($"second-{i}");

        var seg2 = queue.ConsumerAcquire();
        Assert.Equal(3, seg2.Count);
        for (var i = 0; i < 3; i++)
            Assert.Equal($"second-{i}", seg2[i]);
    }

    // ═══════════════════════════ CLEANUP SLAB RECYCLING ══════════════════════

    [Fact]
    public void Cleanup_DoesNotRecycleWhenNextSlabIsNull()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var accessor = queue.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        queue.ConsumerAdvance(segment.Count);

        var handler = new TrackingCleanupHandler();
        queue.ProducerCleanup(ref handler);

        Assert.Equal(SmallSlabLength, handler.CleanedEntries.Count);
        Assert.False(accessor.HasFreeSlabs);
        Assert.Same(accessor.HeadSlab, accessor.TailSlab);
    }

    [Fact]
    public void Cleanup_RecyclesWhenNextSlabExists()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var accessor = queue.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength + 1; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        queue.ConsumerAdvance(segment.Count);

        var handler = new TrackingCleanupHandler();
        queue.ProducerCleanup(ref handler);

        Assert.Equal(SmallSlabLength + 1, handler.CleanedEntries.Count);
        Assert.True(accessor.HasFreeSlabs);
        Assert.Equal(1, accessor.FreeSlabCount);
    }

    [Fact]
    public void Cleanup_RecyclesMultipleSlabs()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var accessor = queue.GetTestAccessor();
        var total = (SmallSlabLength * 3) + 1;

        for (var i = 0; i < total; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        queue.ConsumerAdvance(segment.Count);

        var handler = new TrackingCleanupHandler();
        queue.ProducerCleanup(ref handler);

        Assert.Equal(total, handler.CleanedEntries.Count);
        Assert.Equal(3, accessor.FreeSlabCount);
    }

    [Fact]
    public void Cleanup_DeferredRecycle_EventuallyRecyclesWhenSlabCompletes()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var accessor = queue.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength - 1; i++)
            queue.ProducerAppend($"e{i}");

        var seg1 = queue.ConsumerAcquire();
        queue.ConsumerAdvance(seg1.Count);

        var handler = new TrackingCleanupHandler();
        queue.ProducerCleanup(ref handler);

        Assert.Equal(SmallSlabLength - 1, handler.CleanedEntries.Count);
        Assert.False(accessor.HasFreeSlabs);

        for (var i = SmallSlabLength - 1; i < SmallSlabLength + 2; i++)
            queue.ProducerAppend($"e{i}");

        var seg2 = queue.ConsumerAcquire();
        queue.ConsumerAdvance(seg2.Count);

        queue.ProducerCleanup(ref handler);

        Assert.Equal(SmallSlabLength + 2, handler.CleanedEntries.Count);
        Assert.True(accessor.HasFreeSlabs);
        Assert.Equal(1, accessor.FreeSlabCount);
    }

    [Fact]
    public void RecycledSlabs_AreReusedInOrder()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var accessor = queue.GetTestAccessor();

        for (var i = 0; i < (SmallSlabLength * 2) + 1; i++)
            queue.ProducerAppend($"r1-{i}");

        var seg1 = queue.ConsumerAcquire();
        queue.ConsumerAdvance(seg1.Count);

        var handler = new TrackingCleanupHandler();
        queue.ProducerCleanup(ref handler);
        Assert.Equal(2, accessor.FreeSlabCount);

        for (var i = 0; i < SmallSlabLength * 2; i++)
            queue.ProducerAppend($"r2-{i}");

        Assert.Equal(0, accessor.FreeSlabCount);

        var seg2 = queue.ConsumerAcquire();
        Assert.Equal(SmallSlabLength * 2, seg2.Count);
        for (var i = 0; i < SmallSlabLength * 2; i++)
            Assert.Equal($"r2-{i}", seg2[i]);
    }

    // ═══════════════════════════ INCREMENTAL CYCLES ══════════════════════════

    [Fact]
    public void ManySmallBatches_AcrossMultipleSlabs()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var handler = new TrackingCleanupHandler();
        var totalProduced = 0;

        for (var batch = 0; batch < 10; batch++)
        {
            var batchSize = (batch % 3) + 1;
            for (var i = 0; i < batchSize; i++)
            {
                queue.ProducerAppend($"b{batch}-{i}");
                totalProduced++;
            }

            var segment = queue.ConsumerAcquire();
            Assert.Equal(batchSize, segment.Count);
            Assert.Equal($"b{batch}-0", segment[0]);
            queue.ConsumerAdvance(segment.Count);

            queue.ProducerCleanup(ref handler);
        }

        Assert.Equal(totalProduced, handler.CleanedEntries.Count);

        var accessor = queue.GetTestAccessor();
        Assert.Equal(totalProduced, accessor.CleanupPosition);
    }

    [Fact]
    public void AlternatingProduceAndConsume_NeverLosesData()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var total = SmallSlabLength * 5;

        for (var i = 0; i < total; i++)
        {
            queue.ProducerAppend($"e{i}");

            var segment = queue.ConsumerAcquire();
            Assert.Equal(1, segment.Count);
            Assert.Equal($"e{i}", segment[0]);
            queue.ConsumerAdvance(segment.Count);
        }

        var empty = queue.ConsumerAcquire();
        Assert.True(empty.IsEmpty);
    }

    // ═══════════════════════════ SEGMENT START-MID-SLAB ══════════════════════

    [Fact]
    public void ConsumerAcquiresSegment_StartingMidSlab_SpanningMultipleSlabs()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        for (var i = 0; i < 2; i++)
            queue.ProducerAppend($"skip-{i}");

        var skip = queue.ConsumerAcquire();
        queue.ConsumerAdvance(skip.Count);

        for (var i = 0; i < (SmallSlabLength * 2) + 1; i++)
            queue.ProducerAppend($"read-{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal((SmallSlabLength * 2) + 1, segment.Count);

        for (var i = 0; i < segment.Count; i++)
            Assert.Equal($"read-{i}", segment[i]);

        var collected = new List<string>();
        foreach (ref readonly var item in segment)
            collected.Add(item);

        Assert.Equal(segment.Count, collected.Count);
        for (var i = 0; i < collected.Count; i++)
            Assert.Equal($"read-{i}", collected[i]);
    }

    [Fact]
    public void Segment_Indexer_AcrossFourSlabs_StartingMidSlab()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        queue.ProducerAppend("skip");
        var skip = queue.ConsumerAcquire();
        queue.ConsumerAdvance(skip.Count);

        var count = (SmallSlabLength * 3) + 2;
        for (var i = 0; i < count; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(count, segment.Count);

        Assert.Equal("e0", segment[0]);
        Assert.Equal($"e{count - 1}", segment[count - 1]);

        for (var i = 0; i < count; i++)
            Assert.Equal($"e{i}", segment[i]);
    }

    // ═══════════════════════════ RANGE START AT LAST SLOT OF SLAB ════════════

    [Fact]
    public void SegmentStartsAtLastSlotOfSlab_SpansIntoNextSlab()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        for (var i = 0; i < SmallSlabLength - 1; i++)
            queue.ProducerAppend($"skip-{i}");

        var skip = queue.ConsumerAcquire();
        queue.ConsumerAdvance(skip.Count);

        for (var i = 0; i < 3; i++)
            queue.ProducerAppend($"read-{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(3, segment.Count);
        for (var i = 0; i < 3; i++)
            Assert.Equal($"read-{i}", segment[i]);

        var collected = new List<string>();
        foreach (ref readonly var item in segment)
            collected.Add(item);
        Assert.Equal(3, collected.Count);
    }

    [Fact]
    public void SegmentStartsAtLastSlotOfSlab_SpansFourSlabs()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        for (var i = 0; i < SmallSlabLength - 1; i++)
            queue.ProducerAppend($"skip-{i}");

        var skip = queue.ConsumerAcquire();
        queue.ConsumerAdvance(skip.Count);

        var count = (SmallSlabLength * 3) + 2;
        for (var i = 0; i < count; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(count, segment.Count);

        for (var i = 0; i < count; i++)
            Assert.Equal($"e{i}", segment[i]);

        var collected = new List<string>();
        foreach (ref readonly var item in segment)
            collected.Add(item);
        Assert.Equal(count, collected.Count);
        for (var i = 0; i < count; i++)
            Assert.Equal($"e{i}", collected[i]);
    }

    [Fact]
    public void SegmentStartsAtLastSlotOfSlab_EndsAtExactSlabBoundary()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        for (var i = 0; i < SmallSlabLength - 1; i++)
            queue.ProducerAppend($"skip-{i}");

        var skip = queue.ConsumerAcquire();
        queue.ConsumerAdvance(skip.Count);

        var count = SmallSlabLength + 1;
        for (var i = 0; i < count; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(count, segment.Count);

        Assert.Equal("e0", segment[0]);
        Assert.Equal($"e{count - 1}", segment[count - 1]);
    }

    [Fact]
    public void SegmentStartsAtLastSlotOfSlab_SingleItem()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        for (var i = 0; i < SmallSlabLength - 1; i++)
            queue.ProducerAppend($"skip-{i}");

        var skip = queue.ConsumerAcquire();
        queue.ConsumerAdvance(skip.Count);

        queue.ProducerAppend("only");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(1, segment.Count);
        Assert.Equal("only", segment[0]);
    }

    // ═══════════════════════════ RANGE START IN SECOND SLAB ══════════════════

    [Fact]
    public void SegmentStartsMidSecondSlab_SpansMultipleSlabs()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        for (var i = 0; i < SmallSlabLength + 1; i++)
            queue.ProducerAppend($"skip-{i}");

        var skip = queue.ConsumerAcquire();
        queue.ConsumerAdvance(skip.Count);

        var count = (SmallSlabLength * 3) + 1;
        for (var i = 0; i < count; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(count, segment.Count);

        for (var i = 0; i < count; i++)
            Assert.Equal($"e{i}", segment[i]);

        var collected = new List<string>();
        foreach (ref readonly var item in segment)
            collected.Add(item);
        Assert.Equal(count, collected.Count);
        for (var i = 0; i < count; i++)
            Assert.Equal($"e{i}", collected[i]);
    }

    [Fact]
    public void SegmentStartsAtLastSlotOfSecondSlab_SpansMultipleSlabs()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        for (var i = 0; i < (SmallSlabLength * 2) - 1; i++)
            queue.ProducerAppend($"skip-{i}");

        var skip = queue.ConsumerAcquire();
        queue.ConsumerAdvance(skip.Count);

        var count = (SmallSlabLength * 2) + 3;
        for (var i = 0; i < count; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(count, segment.Count);

        for (var i = 0; i < count; i++)
            Assert.Equal($"e{i}", segment[i]);
    }

    // ═══════════════════════════ RANGE END AT EXACT SLAB BOUNDARY ════════════

    [Fact]
    public void SegmentEndsAtExactSlabBoundary_NoNextSlabExists()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var accessor = queue.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength; i++)
            queue.ProducerAppend($"e{i}");

        Assert.Same(accessor.HeadSlab, accessor.TailSlab);

        var segment = queue.ConsumerAcquire();
        Assert.Equal(SmallSlabLength, segment.Count);
        Assert.Equal($"e{SmallSlabLength - 1}", segment[SmallSlabLength - 1]);
    }

    [Fact]
    public void SegmentEndsAtExactSlabBoundary_NextSlabExists()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var accessor = queue.GetTestAccessor();

        queue.ProducerAppend("skip");
        var skip = queue.ConsumerAcquire();
        queue.ConsumerAdvance(skip.Count);

        for (var i = 0; i < SmallSlabLength; i++)
            queue.ProducerAppend($"e{i}");

        Assert.NotSame(accessor.HeadSlab, accessor.TailSlab);

        var segment = queue.ConsumerAcquire();
        Assert.Equal(SmallSlabLength, segment.Count);
        for (var i = 0; i < SmallSlabLength; i++)
            Assert.Equal($"e{i}", segment[i]);
    }

    [Fact]
    public void SegmentEndsExactlyAtMultiSlabBoundary_NoNextSlab()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var accessor = queue.GetTestAccessor();

        var total = SmallSlabLength * 2;
        for (var i = 0; i < total; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(total, segment.Count);
        Assert.Equal("e0", segment[0]);
        Assert.Equal($"e{total - 1}", segment[total - 1]);

        Assert.Null(accessor.TailSlab.Next);
    }

    // ═══════════════════════════ ADVANCE AT EXACT SLAB BOUNDARY ══════════════

    [Fact]
    public void AdvanceAtExactSlabBoundary_WithNextSlab()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var accessor = queue.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength + 1; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        queue.ConsumerAdvance(segment.Count);

        Assert.Equal(SmallSlabLength + 1, accessor.ConsumerPosition);

        var empty = queue.ConsumerAcquire();
        Assert.True(empty.IsEmpty);
    }

    [Fact]
    public void AdvanceAtExactSlabBoundary_WithoutNextSlab()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var accessor = queue.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        queue.ConsumerAdvance(segment.Count);

        Assert.Equal(SmallSlabLength, accessor.ConsumerPosition);
        Assert.Same(accessor.HeadSlab, accessor.TailSlab);

        queue.ProducerAppend("next");
        var seg2 = queue.ConsumerAcquire();
        Assert.Equal(1, seg2.Count);
        Assert.Equal("next", seg2[0]);
    }

    // ═══════════════════════════ SYSTEMATIC START/END COMBINATIONS ═══════════

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SegmentStartAtEveryOffset_SpansFourPlusSlabs_Indexer(int consumeFirst)
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        for (var i = 0; i < consumeFirst; i++)
            queue.ProducerAppend($"skip-{i}");

        if (consumeFirst > 0)
        {
            var skip = queue.ConsumerAcquire();
            queue.ConsumerAdvance(skip.Count);
        }

        var count = (SmallSlabLength * 4) + 1;
        for (var i = 0; i < count; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(count, segment.Count);

        for (var i = 0; i < count; i++)
            Assert.Equal($"e{i}", segment[i]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SegmentStartAtEveryOffset_SpansFourPlusSlabs_Enumerator(int consumeFirst)
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        for (var i = 0; i < consumeFirst; i++)
            queue.ProducerAppend($"skip-{i}");

        if (consumeFirst > 0)
        {
            var skip = queue.ConsumerAcquire();
            queue.ConsumerAdvance(skip.Count);
        }

        var count = (SmallSlabLength * 4) + 1;
        for (var i = 0; i < count; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        var collected = new List<string>();
        foreach (ref readonly var item in segment)
            collected.Add(item);

        Assert.Equal(count, collected.Count);
        for (var i = 0; i < count; i++)
            Assert.Equal($"e{i}", collected[i]);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(0, 8)]
    [InlineData(1, 3)]
    [InlineData(1, 7)]
    [InlineData(2, 6)]
    [InlineData(3, 1)]
    [InlineData(3, 5)]
    [InlineData(3, 9)]
    [InlineData(3, 13)]
    public void StartOffset_EndCount_Combinations(int consumeFirst, int produceCount)
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        for (var i = 0; i < consumeFirst; i++)
            queue.ProducerAppend($"skip-{i}");

        if (consumeFirst > 0)
        {
            var skip = queue.ConsumerAcquire();
            queue.ConsumerAdvance(skip.Count);
        }

        for (var i = 0; i < produceCount; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(produceCount, segment.Count);

        for (var i = 0; i < produceCount; i++)
            Assert.Equal($"e{i}", segment[i]);

        var collected = new List<string>();
        foreach (ref readonly var item in segment)
            collected.Add(item);
        Assert.Equal(produceCount, collected.Count);
    }

    // ═══════════════════════════ CLEANUP BOUNDARY COMBINATIONS ═══════════════

    [Fact]
    public void Cleanup_AtSlabBoundary_WithNextSlab_RecyclesImmediately()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var accessor = queue.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength + 1; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        queue.ConsumerAdvance(segment.Count);

        var originalHead = accessor.HeadSlab;
        var handler = new TrackingCleanupHandler();
        queue.ProducerCleanup(ref handler);

        Assert.NotSame(originalHead, accessor.HeadSlab);
        Assert.Equal(1, accessor.FreeSlabCount);
    }

    [Fact]
    public void Cleanup_AtSlabBoundary_WithoutNextSlab_DoesNotRecycle()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var accessor = queue.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        queue.ConsumerAdvance(segment.Count);

        var originalHead = accessor.HeadSlab;
        var handler = new TrackingCleanupHandler();
        queue.ProducerCleanup(ref handler);

        Assert.Same(originalHead, accessor.HeadSlab);
        Assert.Equal(0, accessor.FreeSlabCount);
    }

    [Fact]
    public void Cleanup_FourSlabs_RecyclesThree()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var accessor = queue.GetTestAccessor();
        var total = (SmallSlabLength * 4) + 1;

        for (var i = 0; i < total; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        queue.ConsumerAdvance(segment.Count);

        var handler = new TrackingCleanupHandler();
        queue.ProducerCleanup(ref handler);

        Assert.Equal(total, handler.CleanedEntries.Count);
        Assert.Equal(4, accessor.FreeSlabCount);
        Assert.Same(accessor.HeadSlab, accessor.TailSlab);
    }

    [Fact]
    public void Cleanup_SequentialRounds_RecyclesAndReusesAcrossRounds()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var accessor = queue.GetTestAccessor();
        var handler = new TrackingCleanupHandler();

        var r1Count = (SmallSlabLength * 3) + 1;
        for (var i = 0; i < r1Count; i++)
            queue.ProducerAppend($"r1-{i}");

        var seg1 = queue.ConsumerAcquire();
        queue.ConsumerAdvance(seg1.Count);
        queue.ProducerCleanup(ref handler);

        Assert.Equal(3, accessor.FreeSlabCount);

        var r2Count = SmallSlabLength * 2;
        for (var i = 0; i < r2Count; i++)
            queue.ProducerAppend($"r2-{i}");

        Assert.Equal(1, accessor.FreeSlabCount);

        var seg2 = queue.ConsumerAcquire();
        Assert.Equal(r2Count, seg2.Count);
        for (var i = 0; i < r2Count; i++)
            Assert.Equal($"r2-{i}", seg2[i]);
        queue.ConsumerAdvance(seg2.Count);
        queue.ProducerCleanup(ref handler);

        Assert.True(accessor.FreeSlabCount >= 2);

        var r3Count = (SmallSlabLength * 4) + 2;
        for (var i = 0; i < r3Count; i++)
            queue.ProducerAppend($"r3-{i}");

        var seg3 = queue.ConsumerAcquire();
        Assert.Equal(r3Count, seg3.Count);
        for (var i = 0; i < r3Count; i++)
            Assert.Equal($"r3-{i}", seg3[i]);
    }

    // ═══════════════════════════ CLEANUP INVARIANT TESTS ═════════════════════

    [Fact]
    public void Cleanup_ExactBoundary_WithNextExisting_DoesNotRecycle()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        var queue2 = new ProducerConsumerQueue<string>(SmallSlabLength);
        var accessor2 = queue2.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength; i++)
            queue2.ProducerAppend($"e{i}");

        var segment = queue2.ConsumerAcquire();
        queue2.ConsumerAdvance(segment.Count);

        queue2.ProducerAppend("extra");
        Assert.NotSame(accessor2.HeadSlab, accessor2.TailSlab);

        var handler = new TrackingCleanupHandler();
        queue2.ProducerCleanup(ref handler);

        Assert.Equal(SmallSlabLength, handler.CleanedEntries.Count);
        Assert.Equal(0, accessor2.FreeSlabCount);

        var seg2 = queue2.ConsumerAcquire();
        Assert.Equal(1, seg2.Count);
        Assert.Equal("extra", seg2[0]);
        queue2.ConsumerAdvance(seg2.Count);

        queue2.ProducerCleanup(ref handler);
        Assert.Equal(1, accessor2.FreeSlabCount);
    }

    [Fact]
    public void Cleanup_CalledWithNothingToClean_IsNoOp()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var handler = new TrackingCleanupHandler();

        queue.ProducerCleanup(ref handler);
        Assert.Empty(handler.CleanedEntries);
    }

    [Fact]
    public void Cleanup_CalledTwice_SecondCallIsNoOp()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        queue.ProducerAppend("a");
        var segment = queue.ConsumerAcquire();
        queue.ConsumerAdvance(segment.Count);

        var handler = new TrackingCleanupHandler();
        queue.ProducerCleanup(ref handler);
        Assert.Single(handler.CleanedEntries);

        queue.ProducerCleanup(ref handler);
        Assert.Single(handler.CleanedEntries);
    }

    [Fact]
    public void RecycledSlab_HasZeroedEntries()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var accessor = queue.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength + 1; i++)
            queue.ProducerAppend($"e{i}");

        var originalHead = accessor.HeadSlab;

        var segment = queue.ConsumerAcquire();
        queue.ConsumerAdvance(segment.Count);

        var handler = new TrackingCleanupHandler();
        queue.ProducerCleanup(ref handler);

        Assert.True(accessor.HasFreeSlabs);

        for (var i = 0; i < SmallSlabLength; i++)
            Assert.Null(originalHead.Entries[i]);
    }

    // ═══════════════════════════ CONSUMER SLAB ADVANCEMENT ═══════════════════

    [Fact]
    public void ConsumerSlab_AdvancesMultipleSlabs_WhenFarBehind()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        var total = SmallSlabLength * 5;
        for (var i = 0; i < total; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        Assert.Equal(total, segment.Count);

        for (var i = 0; i < total; i++)
            Assert.Equal($"e{i}", segment[i]);

        queue.ConsumerAdvance(segment.Count);

        var empty = queue.ConsumerAcquire();
        Assert.True(empty.IsEmpty);
    }

    [Fact]
    public void ConsumerSlab_AdvancesMultipleSlabs_AcrossMultipleAcquireCycles()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        var total = SmallSlabLength * 4;
        for (var i = 0; i < total; i++)
            queue.ProducerAppend($"e{i}");

        var seg1 = queue.ConsumerAcquire();
        Assert.Equal(total, seg1.Count);
        queue.ConsumerAdvance(seg1.Count);

        for (var i = total; i < total + SmallSlabLength; i++)
            queue.ProducerAppend($"e{i}");

        var seg2 = queue.ConsumerAcquire();
        Assert.Equal(SmallSlabLength, seg2.Count);
        Assert.Equal($"e{total}", seg2[0]);
    }

    // ═══════════════════════════ INTERLEAVING PATTERNS ═══════════════════════

    [Fact]
    public void ProduceAcquireProduceMore_ThenAdvance_ThenAcquireAgain()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);

        for (var i = 0; i < SmallSlabLength + 2; i++)
            queue.ProducerAppend($"batch1-{i}");

        var seg1 = queue.ConsumerAcquire();
        Assert.Equal(SmallSlabLength + 2, seg1.Count);

        for (var i = 0; i < SmallSlabLength; i++)
            queue.ProducerAppend($"batch2-{i}");

        queue.ConsumerAdvance(seg1.Count);

        var seg2 = queue.ConsumerAcquire();
        Assert.Equal(SmallSlabLength, seg2.Count);
        for (var i = 0; i < SmallSlabLength; i++)
            Assert.Equal($"batch2-{i}", seg2[i]);
    }

    [Fact]
    public void Cleanup_DeferredAcrossManySlabs()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var accessor = queue.GetTestAccessor();

        var total = (SmallSlabLength * 6) + 2;
        for (var i = 0; i < total; i++)
            queue.ProducerAppend($"e{i}");

        var segment = queue.ConsumerAcquire();
        queue.ConsumerAdvance(segment.Count);

        var handler = new TrackingCleanupHandler();
        queue.ProducerCleanup(ref handler);

        Assert.Equal(total, handler.CleanedEntries.Count);
        Assert.Equal(6, accessor.FreeSlabCount);
    }

    [Fact]
    public void Cleanup_Delayed_ProducerAppendsContinue()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var accessor = queue.GetTestAccessor();

        for (var round = 0; round < 5; round++)
        {
            for (var i = 0; i < SmallSlabLength; i++)
                queue.ProducerAppend($"r{round}-{i}");

            var segment = queue.ConsumerAcquire();
            queue.ConsumerAdvance(segment.Count);
        }

        Assert.Equal(0, accessor.CleanupPosition);

        var handler = new TrackingCleanupHandler();
        queue.ProducerCleanup(ref handler);

        Assert.Equal(SmallSlabLength * 5, handler.CleanedEntries.Count);
        Assert.Equal(SmallSlabLength * 5, accessor.CleanupPosition);
        Assert.True(accessor.FreeSlabCount > 0);
    }
}
