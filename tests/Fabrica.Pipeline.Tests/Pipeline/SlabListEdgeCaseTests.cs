using Xunit;

namespace Fabrica.Pipeline.Tests.Pipeline;

/// <summary>
/// Edge case tests for <see cref="SlabList{TPayload}"/> using small slab lengths (typically 4) to make multi-slab scenarios easy
/// to exercise without producing thousands of entries. These tests complement <see cref="SlabListTests"/> which uses the default
/// LOH-aware slab length.
/// </summary>
public class SlabListEdgeCaseTests
{
    private const int SmallSlabLength = 4;

    private static PipelineEntry<string> MakeEntry(string payload, long time = 0) =>
        new() { Payload = payload, PublishTimeNanoseconds = time };

    private readonly struct TrackingCleanupHandler : IEntryCleanupHandler<string>
    {
        public List<(long Position, string Payload)> CleanedEntries { get; }

        public TrackingCleanupHandler() => this.CleanedEntries = [];

        public readonly void HandleEntry(long position, in PipelineEntry<string> entry) =>
            this.CleanedEntries.Add((position, entry.Payload));
    }

    // ═══════════════════════════ SLAB LENGTH CONFIG ══════════════════════════

    [Fact]
    public void InternalConstructor_UsesProvidedSlabLength()
    {
        var list = new SlabList<string>(SmallSlabLength);
        var accessor = list.GetTestAccessor();
        Assert.Equal(SmallSlabLength, accessor.SlabLength);
    }

    // ═══════════════════════════ CONSUMER READS WITHIN SINGLE SLAB ═══════════

    [Fact]
    public void ConsumerReadsPartialFirstSlab()
    {
        var list = new SlabList<string>(SmallSlabLength);

        list.ProducerAppendEntry(MakeEntry("a"));
        list.ProducerAppendEntry(MakeEntry("b"));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(2, range.Count);
        Assert.Equal("a", range[0].Payload);
        Assert.Equal("b", range[1].Payload);
    }

    [Fact]
    public void ConsumerReadsExactlyOneFullSlab()
    {
        var list = new SlabList<string>(SmallSlabLength);

        for (var i = 0; i < SmallSlabLength; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(SmallSlabLength, range.Count);
        for (var i = 0; i < SmallSlabLength; i++)
            Assert.Equal($"e{i}", range[i].Payload);
    }

    // ═══════════════════════════ CONSUMER READS OVERLAPPING INTO SECOND SLAB ═

    [Fact]
    public void ConsumerReadsOverlappingIntoSecondSlab()
    {
        var list = new SlabList<string>(SmallSlabLength);

        for (var i = 0; i < SmallSlabLength + 2; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(SmallSlabLength + 2, range.Count);
        for (var i = 0; i < SmallSlabLength + 2; i++)
            Assert.Equal($"e{i}", range[i].Payload);
    }

    [Fact]
    public void ConsumerReadsExactlyTwoFullSlabs()
    {
        var list = new SlabList<string>(SmallSlabLength);
        var total = SmallSlabLength * 2;

        for (var i = 0; i < total; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(total, range.Count);
        for (var i = 0; i < total; i++)
            Assert.Equal($"e{i}", range[i].Payload);
    }

    // ═══════════════════════════ CONSUMER READS SPANNING 3+ SLABS ════════════

    [Fact]
    public void ConsumerReadsSpanningThreeSlabs()
    {
        var list = new SlabList<string>(SmallSlabLength);
        var total = (SmallSlabLength * 2) + 2;

        for (var i = 0; i < total; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(total, range.Count);
        for (var i = 0; i < total; i++)
            Assert.Equal($"e{i}", range[i].Payload);
    }

    [Fact]
    public void ConsumerReadsSpanningFiveSlabs()
    {
        var list = new SlabList<string>(SmallSlabLength);
        var total = (SmallSlabLength * 4) + 3;

        for (var i = 0; i < total; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}", i));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(total, range.Count);

        var index = 0;
        foreach (ref readonly var entry in range)
        {
            Assert.Equal($"e{index}", entry.Payload);
            Assert.Equal(index, entry.PublishTimeNanoseconds);
            index++;
        }

        Assert.Equal(total, index);
    }

    // ═══════════════════════════ SLABRANGE ACROSS MANY SLABS ═════════════════

    [Fact]
    public void SlabRange_Indexer_FirstEntryOfEachSlab()
    {
        var list = new SlabList<string>(SmallSlabLength);
        var total = SmallSlabLength * 3;

        for (var i = 0; i < total; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal($"e0", range[0].Payload);
        Assert.Equal($"e{SmallSlabLength}", range[SmallSlabLength].Payload);
        Assert.Equal($"e{SmallSlabLength * 2}", range[SmallSlabLength * 2].Payload);
    }

    [Fact]
    public void SlabRange_Indexer_LastEntryOfEachSlab()
    {
        var list = new SlabList<string>(SmallSlabLength);
        var total = SmallSlabLength * 3;

        for (var i = 0; i < total; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal($"e{SmallSlabLength - 1}", range[SmallSlabLength - 1].Payload);
        Assert.Equal($"e{(SmallSlabLength * 2) - 1}", range[(SmallSlabLength * 2) - 1].Payload);
        Assert.Equal($"e{(SmallSlabLength * 3) - 1}", range[(SmallSlabLength * 3) - 1].Payload);
    }

    [Fact]
    public void SlabRange_Enumerator_AcrossThreeSlabs()
    {
        var list = new SlabList<string>(SmallSlabLength);
        var total = (SmallSlabLength * 3) + 1;

        for (var i = 0; i < total; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        var collected = new List<string>();
        foreach (ref readonly var entry in range)
            collected.Add(entry.Payload);

        Assert.Equal(total, collected.Count);
        for (var i = 0; i < total; i++)
            Assert.Equal($"e{i}", collected[i]);
    }

    // ═══════════════════════════ PARTIAL CONSUME THEN ACQUIRE ACROSS SLABS ═══

    [Fact]
    public void ConsumerReleasesPartialSlab_ThenAcquiresAcrossSlabBoundary()
    {
        var list = new SlabList<string>(SmallSlabLength);

        for (var i = 0; i < 2; i++)
            list.ProducerAppendEntry(MakeEntry($"batch1-{i}"));

        var range1 = list.ConsumerAcquireEntries();
        Assert.Equal(2, range1.Count);
        list.ConsumerReleaseEntries(in range1);

        for (var i = 0; i < SmallSlabLength; i++)
            list.ProducerAppendEntry(MakeEntry($"batch2-{i}"));

        var range2 = list.ConsumerAcquireEntries();
        Assert.Equal(SmallSlabLength, range2.Count);
        Assert.Equal("batch2-0", range2[0].Payload);
        Assert.Equal($"batch2-{SmallSlabLength - 1}", range2[SmallSlabLength - 1].Payload);
    }

    [Fact]
    public void ConsumerReleasesAtSlabBoundary_NextAcquireStartsOnNewSlab()
    {
        var list = new SlabList<string>(SmallSlabLength);

        for (var i = 0; i < SmallSlabLength; i++)
            list.ProducerAppendEntry(MakeEntry($"first-{i}"));

        var range1 = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range1);

        for (var i = 0; i < 3; i++)
            list.ProducerAppendEntry(MakeEntry($"second-{i}"));

        var range2 = list.ConsumerAcquireEntries();
        Assert.Equal(3, range2.Count);
        for (var i = 0; i < 3; i++)
            Assert.Equal($"second-{i}", range2[i].Payload);
    }

    // ═══════════════════════════ CLEANUP SLAB RECYCLING ══════════════════════

    [Fact]
    public void Cleanup_DoesNotRecycleWhenNextSlabIsNull()
    {
        var list = new SlabList<string>(SmallSlabLength);
        var accessor = list.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range);

        var handler = new TrackingCleanupHandler();
        list.ProducerCleanupReleasedEntries(ref handler);

        Assert.Equal(SmallSlabLength, handler.CleanedEntries.Count);
        Assert.False(accessor.HasFreeSlabs);
        Assert.Same(accessor.HeadSlab, accessor.TailSlab);
    }

    [Fact]
    public void Cleanup_RecyclesWhenNextSlabExists()
    {
        var list = new SlabList<string>(SmallSlabLength);
        var accessor = list.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength + 1; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range);

        var handler = new TrackingCleanupHandler();
        list.ProducerCleanupReleasedEntries(ref handler);

        Assert.Equal(SmallSlabLength + 1, handler.CleanedEntries.Count);
        Assert.True(accessor.HasFreeSlabs);
        Assert.Equal(1, accessor.FreeSlabCount);
    }

    [Fact]
    public void Cleanup_RecyclesMultipleSlabs()
    {
        var list = new SlabList<string>(SmallSlabLength);
        var accessor = list.GetTestAccessor();
        var total = (SmallSlabLength * 3) + 1;

        for (var i = 0; i < total; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range);

        var handler = new TrackingCleanupHandler();
        list.ProducerCleanupReleasedEntries(ref handler);

        Assert.Equal(total, handler.CleanedEntries.Count);
        Assert.Equal(3, accessor.FreeSlabCount);
    }

    [Fact]
    public void Cleanup_DeferredRecycle_EventuallyRecyclesWhenSlabCompletes()
    {
        var list = new SlabList<string>(SmallSlabLength);
        var accessor = list.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength - 1; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range1 = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range1);

        var handler = new TrackingCleanupHandler();
        list.ProducerCleanupReleasedEntries(ref handler);

        Assert.Equal(SmallSlabLength - 1, handler.CleanedEntries.Count);
        Assert.False(accessor.HasFreeSlabs);

        for (var i = SmallSlabLength - 1; i < SmallSlabLength + 2; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range2 = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range2);

        list.ProducerCleanupReleasedEntries(ref handler);

        Assert.Equal(SmallSlabLength + 2, handler.CleanedEntries.Count);
        Assert.True(accessor.HasFreeSlabs);
        Assert.Equal(1, accessor.FreeSlabCount);
    }

    [Fact]
    public void RecycledSlabs_AreReusedInOrder()
    {
        var list = new SlabList<string>(SmallSlabLength);
        var accessor = list.GetTestAccessor();

        for (var i = 0; i < (SmallSlabLength * 2) + 1; i++)
            list.ProducerAppendEntry(MakeEntry($"r1-{i}"));

        var range1 = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range1);

        var handler = new TrackingCleanupHandler();
        list.ProducerCleanupReleasedEntries(ref handler);
        Assert.Equal(2, accessor.FreeSlabCount);

        for (var i = 0; i < SmallSlabLength * 2; i++)
            list.ProducerAppendEntry(MakeEntry($"r2-{i}"));

        Assert.Equal(0, accessor.FreeSlabCount);

        var range2 = list.ConsumerAcquireEntries();
        Assert.Equal(SmallSlabLength * 2, range2.Count);
        for (var i = 0; i < SmallSlabLength * 2; i++)
            Assert.Equal($"r2-{i}", range2[i].Payload);
    }

    // ═══════════════════════════ INCREMENTAL CYCLES ══════════════════════════

    [Fact]
    public void ManySmallBatches_AcrossMultipleSlabs()
    {
        var list = new SlabList<string>(SmallSlabLength);
        var handler = new TrackingCleanupHandler();
        var totalProduced = 0;

        for (var batch = 0; batch < 10; batch++)
        {
            var batchSize = (batch % 3) + 1;
            for (var i = 0; i < batchSize; i++)
            {
                list.ProducerAppendEntry(MakeEntry($"b{batch}-{i}"));
                totalProduced++;
            }

            var range = list.ConsumerAcquireEntries();
            Assert.Equal(batchSize, range.Count);
            Assert.Equal($"b{batch}-0", range[0].Payload);
            list.ConsumerReleaseEntries(in range);

            list.ProducerCleanupReleasedEntries(ref handler);
        }

        Assert.Equal(totalProduced, handler.CleanedEntries.Count);

        var accessor = list.GetTestAccessor();
        Assert.Equal(totalProduced, accessor.CleanupPosition);
    }

    [Fact]
    public void AlternatingProduceAndConsume_NeverLosesData()
    {
        var list = new SlabList<string>(SmallSlabLength);
        var total = SmallSlabLength * 5;

        for (var i = 0; i < total; i++)
        {
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

            var range = list.ConsumerAcquireEntries();
            Assert.Equal(1, range.Count);
            Assert.Equal($"e{i}", range[0].Payload);
            list.ConsumerReleaseEntries(in range);
        }

        var empty = list.ConsumerAcquireEntries();
        Assert.True(empty.IsEmpty);
    }

    // ═══════════════════════════ SLABRANGE START-MID-SLAB ════════════════════

    [Fact]
    public void ConsumerAcquiresRange_StartingMidSlab_SpanningMultipleSlabs()
    {
        var list = new SlabList<string>(SmallSlabLength);

        for (var i = 0; i < 2; i++)
            list.ProducerAppendEntry(MakeEntry($"skip-{i}"));

        var skip = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in skip);

        for (var i = 0; i < (SmallSlabLength * 2) + 1; i++)
            list.ProducerAppendEntry(MakeEntry($"read-{i}"));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal((SmallSlabLength * 2) + 1, range.Count);

        for (var i = 0; i < range.Count; i++)
            Assert.Equal($"read-{i}", range[i].Payload);

        var collected = new List<string>();
        foreach (ref readonly var entry in range)
            collected.Add(entry.Payload);

        Assert.Equal(range.Count, collected.Count);
        for (var i = 0; i < collected.Count; i++)
            Assert.Equal($"read-{i}", collected[i]);
    }

    [Fact]
    public void SlabRange_Indexer_AcrossFourSlabs_StartingMidSlab()
    {
        var list = new SlabList<string>(SmallSlabLength);

        list.ProducerAppendEntry(MakeEntry("skip"));
        var skip = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in skip);

        var count = (SmallSlabLength * 3) + 2;
        for (var i = 0; i < count; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(count, range.Count);

        Assert.Equal("e0", range[0].Payload);
        Assert.Equal($"e{count - 1}", range[count - 1].Payload);

        for (var i = 0; i < count; i++)
            Assert.Equal($"e{i}", range[i].Payload);
    }
}
