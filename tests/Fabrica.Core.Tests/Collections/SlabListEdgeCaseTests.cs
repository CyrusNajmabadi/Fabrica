using Fabrica.Core.Collections;
using Xunit;

namespace Fabrica.Core.Tests.Collections;

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

    // ═══════════════════════════ RANGE START AT LAST SLOT OF SLAB ════════════

    [Fact]
    public void RangeStartsAtLastSlotOfSlab_SpansIntoNextSlab()
    {
        // Consume 3 entries (offset 0-2), so next acquire starts at offset 3 — the last slot of slab 0.
        var list = new SlabList<string>(SmallSlabLength);

        for (var i = 0; i < SmallSlabLength - 1; i++)
            list.ProducerAppendEntry(MakeEntry($"skip-{i}"));

        var skip = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in skip);

        for (var i = 0; i < 3; i++)
            list.ProducerAppendEntry(MakeEntry($"read-{i}"));

        // Range starts at offset 3 in slab 0, spans into slab 1.
        var range = list.ConsumerAcquireEntries();
        Assert.Equal(3, range.Count);
        for (var i = 0; i < 3; i++)
            Assert.Equal($"read-{i}", range[i].Payload);

        var collected = new List<string>();
        foreach (ref readonly var entry in range)
            collected.Add(entry.Payload);
        Assert.Equal(3, collected.Count);
    }

    [Fact]
    public void RangeStartsAtLastSlotOfSlab_SpansFourSlabs()
    {
        // Consume 3 entries so range starts at offset 3. Then produce enough to span 4 slabs.
        var list = new SlabList<string>(SmallSlabLength);

        for (var i = 0; i < SmallSlabLength - 1; i++)
            list.ProducerAppendEntry(MakeEntry($"skip-{i}"));

        var skip = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in skip);

        // 1 remaining in slab 0 + 4 in slab 1 + 4 in slab 2 + 4 in slab 3 + 1 in slab 4 = 14 entries across 5 slabs
        var count = (SmallSlabLength * 3) + 2;
        for (var i = 0; i < count; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(count, range.Count);

        for (var i = 0; i < count; i++)
            Assert.Equal($"e{i}", range[i].Payload);

        var collected = new List<string>();
        foreach (ref readonly var entry in range)
            collected.Add(entry.Payload);
        Assert.Equal(count, collected.Count);
        for (var i = 0; i < count; i++)
            Assert.Equal($"e{i}", collected[i]);
    }

    [Fact]
    public void RangeStartsAtLastSlotOfSlab_EndsAtExactSlabBoundary()
    {
        // Consume 3 entries so range starts at offset 3. Produce exactly 5 more so range ends at
        // offset 3 of slab 1 (position 7, the exact boundary of slab 1).
        var list = new SlabList<string>(SmallSlabLength);

        for (var i = 0; i < SmallSlabLength - 1; i++)
            list.ProducerAppendEntry(MakeEntry($"skip-{i}"));

        var skip = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in skip);

        // 1 remaining in slab 0 + 4 in slab 1 = exactly fills through end of slab 1
        var count = SmallSlabLength + 1;
        for (var i = 0; i < count; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(count, range.Count);

        Assert.Equal("e0", range[0].Payload);
        Assert.Equal($"e{count - 1}", range[count - 1].Payload);
    }

    [Fact]
    public void RangeStartsAtLastSlotOfSlab_SingleEntry()
    {
        // Edge case: range is exactly 1 entry sitting at the last offset of a slab.
        var list = new SlabList<string>(SmallSlabLength);

        for (var i = 0; i < SmallSlabLength - 1; i++)
            list.ProducerAppendEntry(MakeEntry($"skip-{i}"));

        var skip = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in skip);

        list.ProducerAppendEntry(MakeEntry("only"));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(1, range.Count);
        Assert.Equal("only", range[0].Payload);
    }

    // ═══════════════════════════ RANGE START IN SECOND SLAB ══════════════════

    [Fact]
    public void RangeStartsMidSecondSlab_SpansMultipleSlabs()
    {
        // Consume through position 5 (offset 1 in slab 1). Then produce across 3+ more slabs.
        var list = new SlabList<string>(SmallSlabLength);

        for (var i = 0; i < SmallSlabLength + 1; i++)
            list.ProducerAppendEntry(MakeEntry($"skip-{i}"));

        var skip = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in skip);

        // Range starts at position 5 (offset 1 in slab 1).
        var count = (SmallSlabLength * 3) + 1;
        for (var i = 0; i < count; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(count, range.Count);

        for (var i = 0; i < count; i++)
            Assert.Equal($"e{i}", range[i].Payload);

        var collected = new List<string>();
        foreach (ref readonly var entry in range)
            collected.Add(entry.Payload);
        Assert.Equal(count, collected.Count);
        for (var i = 0; i < count; i++)
            Assert.Equal($"e{i}", collected[i]);
    }

    [Fact]
    public void RangeStartsAtLastSlotOfSecondSlab_SpansMultipleSlabs()
    {
        // Consume through position 7 (offset 3 in slab 1, the very last slot). Then produce across slabs.
        var list = new SlabList<string>(SmallSlabLength);

        for (var i = 0; i < (SmallSlabLength * 2) - 1; i++)
            list.ProducerAppendEntry(MakeEntry($"skip-{i}"));

        var skip = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in skip);

        var count = (SmallSlabLength * 2) + 3;
        for (var i = 0; i < count; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(count, range.Count);

        for (var i = 0; i < count; i++)
            Assert.Equal($"e{i}", range[i].Payload);
    }

    // ═══════════════════════════ RANGE END AT EXACT SLAB BOUNDARY ════════════

    [Fact]
    public void RangeEndsAtExactSlabBoundary_NoNextSlabExists()
    {
        // Produce exactly one full slab (4 entries). No slab 1 exists.
        // Range covers all of slab 0, ending at offset 3.
        var list = new SlabList<string>(SmallSlabLength);
        var accessor = list.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        Assert.Same(accessor.HeadSlab, accessor.TailSlab);

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(SmallSlabLength, range.Count);
        Assert.Equal($"e{SmallSlabLength - 1}", range[SmallSlabLength - 1].Payload);
    }

    [Fact]
    public void RangeEndsAtExactSlabBoundary_NextSlabExists()
    {
        // Produce 4 entries (fills slab 0) + 1 more (creates slab 1). Consumer was already at
        // position 1, so it acquires 4 entries ending at the exact slab 0/1 boundary while slab 1 exists.
        var list = new SlabList<string>(SmallSlabLength);
        var accessor = list.GetTestAccessor();

        list.ProducerAppendEntry(MakeEntry("skip"));
        var skip = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in skip);

        // Produce 4 entries at positions 1-4. Position 4 triggers slab 1 creation.
        // Range covers positions 1-4: offsets 1,2,3 in slab 0 and offset 0 in slab 1.
        for (var i = 0; i < SmallSlabLength; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        Assert.NotSame(accessor.HeadSlab, accessor.TailSlab);

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(SmallSlabLength, range.Count);
        for (var i = 0; i < SmallSlabLength; i++)
            Assert.Equal($"e{i}", range[i].Payload);
    }

    [Fact]
    public void RangeEndsExactlyAtMultiSlabBoundary_NoNextSlab()
    {
        // Produce exactly 2 full slabs worth (8 entries). Slab 2 does not exist.
        var list = new SlabList<string>(SmallSlabLength);
        var accessor = list.GetTestAccessor();

        var total = SmallSlabLength * 2;
        for (var i = 0; i < total; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(total, range.Count);
        Assert.Equal("e0", range[0].Payload);
        Assert.Equal($"e{total - 1}", range[total - 1].Payload);

        // Verify: tail IS slab 1 (second slab) and no slab beyond it
        Assert.Null(accessor.TailSlab.Next);
    }

    // ═══════════════════════════ RELEASE AT EXACT SLAB BOUNDARY ══════════════

    [Fact]
    public void ReleaseAtExactSlabBoundary_WithNextSlab()
    {
        // Produce 5 entries (slab 0 full, slab 1 has 1). Acquire all 5, release.
        // Consumer position is 5 which is past the slab 0 boundary — consumer slab should advance.
        var list = new SlabList<string>(SmallSlabLength);
        var accessor = list.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength + 1; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range);

        Assert.Equal(SmallSlabLength + 1, accessor.ConsumerPosition);

        // Next acquire should be empty and not crash.
        var empty = list.ConsumerAcquireEntries();
        Assert.True(empty.IsEmpty);
    }

    [Fact]
    public void ReleaseAtExactSlabBoundary_WithoutNextSlab()
    {
        // Produce exactly 4 entries (fills slab 0 exactly). No slab 1. Release at boundary.
        var list = new SlabList<string>(SmallSlabLength);
        var accessor = list.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range);

        Assert.Equal(SmallSlabLength, accessor.ConsumerPosition);
        Assert.Same(accessor.HeadSlab, accessor.TailSlab);

        // Produce more — should create slab 1. Consumer can acquire from it.
        list.ProducerAppendEntry(MakeEntry("next"));
        var range2 = list.ConsumerAcquireEntries();
        Assert.Equal(1, range2.Count);
        Assert.Equal("next", range2[0].Payload);
    }

    // ═══════════════════════════ SYSTEMATIC START/END COMBINATIONS ═══════════

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RangeStartAtEveryOffset_SpansFourPlusSlabs_Indexer(int consumeFirst)
    {
        // consumeFirst controls the start offset within the first slab.
        var list = new SlabList<string>(SmallSlabLength);

        for (var i = 0; i < consumeFirst; i++)
            list.ProducerAppendEntry(MakeEntry($"skip-{i}"));

        if (consumeFirst > 0)
        {
            var skip = list.ConsumerAcquireEntries();
            list.ConsumerReleaseEntries(in skip);
        }

        // Produce enough to span 4+ slabs from the start offset.
        var count = (SmallSlabLength * 4) + 1;
        for (var i = 0; i < count; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(count, range.Count);

        for (var i = 0; i < count; i++)
            Assert.Equal($"e{i}", range[i].Payload);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RangeStartAtEveryOffset_SpansFourPlusSlabs_Enumerator(int consumeFirst)
    {
        var list = new SlabList<string>(SmallSlabLength);

        for (var i = 0; i < consumeFirst; i++)
            list.ProducerAppendEntry(MakeEntry($"skip-{i}"));

        if (consumeFirst > 0)
        {
            var skip = list.ConsumerAcquireEntries();
            list.ConsumerReleaseEntries(in skip);
        }

        var count = (SmallSlabLength * 4) + 1;
        for (var i = 0; i < count; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        var collected = new List<string>();
        foreach (ref readonly var entry in range)
            collected.Add(entry.Payload);

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
        var list = new SlabList<string>(SmallSlabLength);

        for (var i = 0; i < consumeFirst; i++)
            list.ProducerAppendEntry(MakeEntry($"skip-{i}"));

        if (consumeFirst > 0)
        {
            var skip = list.ConsumerAcquireEntries();
            list.ConsumerReleaseEntries(in skip);
        }

        for (var i = 0; i < produceCount; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(produceCount, range.Count);

        for (var i = 0; i < produceCount; i++)
            Assert.Equal($"e{i}", range[i].Payload);

        var collected = new List<string>();
        foreach (ref readonly var entry in range)
            collected.Add(entry.Payload);
        Assert.Equal(produceCount, collected.Count);
    }

    // ═══════════════════════════ CLEANUP BOUNDARY COMBINATIONS ═══════════════

    [Fact]
    public void Cleanup_AtSlabBoundary_WithNextSlab_RecyclesImmediately()
    {
        // Fill slab 0 + 1 entry in slab 1. Consumer releases all. Cleanup should recycle slab 0.
        var list = new SlabList<string>(SmallSlabLength);
        var accessor = list.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength + 1; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range);

        var originalHead = accessor.HeadSlab;
        var handler = new TrackingCleanupHandler();
        list.ProducerCleanupReleasedEntries(ref handler);

        Assert.NotSame(originalHead, accessor.HeadSlab);
        Assert.Equal(1, accessor.FreeSlabCount);
    }

    [Fact]
    public void Cleanup_AtSlabBoundary_WithoutNextSlab_DoesNotRecycle()
    {
        // Fill slab 0 exactly. Consume, release. Next slab doesn't exist. Head stays.
        var list = new SlabList<string>(SmallSlabLength);
        var accessor = list.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range);

        var originalHead = accessor.HeadSlab;
        var handler = new TrackingCleanupHandler();
        list.ProducerCleanupReleasedEntries(ref handler);

        Assert.Same(originalHead, accessor.HeadSlab);
        Assert.Equal(0, accessor.FreeSlabCount);
    }

    [Fact]
    public void Cleanup_FourSlabs_RecyclesThree()
    {
        // Produce 4 full slabs + 1, so 4 slabs are fully consumed. Cleanup recycles all 4 except the tail.
        var list = new SlabList<string>(SmallSlabLength);
        var accessor = list.GetTestAccessor();
        var total = (SmallSlabLength * 4) + 1;

        for (var i = 0; i < total; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range);

        var handler = new TrackingCleanupHandler();
        list.ProducerCleanupReleasedEntries(ref handler);

        Assert.Equal(total, handler.CleanedEntries.Count);
        Assert.Equal(4, accessor.FreeSlabCount);
        Assert.Same(accessor.HeadSlab, accessor.TailSlab);
    }

    [Fact]
    public void Cleanup_SequentialRounds_RecyclesAndReusesAcrossRounds()
    {
        var list = new SlabList<string>(SmallSlabLength);
        var accessor = list.GetTestAccessor();
        var handler = new TrackingCleanupHandler();

        // Round 1: fill 3 slabs + 1 entry. Consume, cleanup.
        var r1Count = (SmallSlabLength * 3) + 1;
        for (var i = 0; i < r1Count; i++)
            list.ProducerAppendEntry(MakeEntry($"r1-{i}"));

        var range1 = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range1);
        list.ProducerCleanupReleasedEntries(ref handler);

        Assert.Equal(3, accessor.FreeSlabCount);

        // Round 2: fill 2 slabs (reuses 2 from free stack). Consume, cleanup.
        var r2Count = SmallSlabLength * 2;
        for (var i = 0; i < r2Count; i++)
            list.ProducerAppendEntry(MakeEntry($"r2-{i}"));

        Assert.Equal(1, accessor.FreeSlabCount);

        var range2 = list.ConsumerAcquireEntries();
        Assert.Equal(r2Count, range2.Count);
        for (var i = 0; i < r2Count; i++)
            Assert.Equal($"r2-{i}", range2[i].Payload);
        list.ConsumerReleaseEntries(in range2);
        list.ProducerCleanupReleasedEntries(ref handler);

        // Original tail from round 1 and the 2 slabs from round 2 should be recycled,
        // plus the 1 left over from round 1.
        Assert.True(accessor.FreeSlabCount >= 2);

        // Round 3: fill 4 slabs to verify everything still works.
        var r3Count = (SmallSlabLength * 4) + 2;
        for (var i = 0; i < r3Count; i++)
            list.ProducerAppendEntry(MakeEntry($"r3-{i}"));

        var range3 = list.ConsumerAcquireEntries();
        Assert.Equal(r3Count, range3.Count);
        for (var i = 0; i < r3Count; i++)
            Assert.Equal($"r3-{i}", range3[i].Payload);
    }

    // ═══════════════════════════ CLEANUP INVARIANT TESTS ═════════════════════

    [Fact]
    public void Cleanup_ExactBoundary_WithNextExisting_DoesNotRecycle()
    {
        // Fill slab 0 + 1 entry in slab 1 (so Next exists). Consumer releases ONLY through the
        // exact slab 0 boundary (position 4). Cleanup cleans 0-3, _cleanupPosition becomes 4,
        // but the loop exits (4 < 4 is false) before the recycle check fires.
        var list = new SlabList<string>(SmallSlabLength);
        var accessor = list.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength + 1; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        // Acquire all 5 but only release the first 4 (exact boundary).
        // We can't partially release, so produce 4, consume 4, then produce 1 more.
        var list2 = new SlabList<string>(SmallSlabLength);
        var accessor2 = list2.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength; i++)
            list2.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list2.ConsumerAcquireEntries();
        list2.ConsumerReleaseEntries(in range);

        // Now produce 1 more — creates slab 1. Next exists, but consumerPosition == 4 (boundary).
        list2.ProducerAppendEntry(MakeEntry("extra"));
        Assert.NotSame(accessor2.HeadSlab, accessor2.TailSlab);

        var handler = new TrackingCleanupHandler();
        list2.ProducerCleanupReleasedEntries(ref handler);

        // Slab 0 is NOT recycled because cleanup loop exits at _cleanupPosition=4 == consumerPosition=4.
        Assert.Equal(SmallSlabLength, handler.CleanedEntries.Count);
        Assert.Equal(0, accessor2.FreeSlabCount);

        // After consumer acquires the extra entry and releases, cleanup CAN recycle.
        var range2 = list2.ConsumerAcquireEntries();
        Assert.Equal(1, range2.Count);
        Assert.Equal("extra", range2[0].Payload);
        list2.ConsumerReleaseEntries(in range2);

        list2.ProducerCleanupReleasedEntries(ref handler);
        Assert.Equal(1, accessor2.FreeSlabCount);
    }

    [Fact]
    public void Cleanup_CalledWithNothingToClean_IsNoOp()
    {
        var list = new SlabList<string>(SmallSlabLength);
        var handler = new TrackingCleanupHandler();

        list.ProducerCleanupReleasedEntries(ref handler);
        Assert.Empty(handler.CleanedEntries);
    }

    [Fact]
    public void Cleanup_CalledTwice_SecondCallIsNoOp()
    {
        var list = new SlabList<string>(SmallSlabLength);

        list.ProducerAppendEntry(MakeEntry("a"));
        var range = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range);

        var handler = new TrackingCleanupHandler();
        list.ProducerCleanupReleasedEntries(ref handler);
        Assert.Single(handler.CleanedEntries);

        list.ProducerCleanupReleasedEntries(ref handler);
        Assert.Single(handler.CleanedEntries);
    }

    [Fact]
    public void RecycledSlab_HasZeroedEntries()
    {
        var list = new SlabList<string>(SmallSlabLength);
        var accessor = list.GetTestAccessor();

        for (var i = 0; i < SmallSlabLength + 1; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var originalHead = accessor.HeadSlab;

        var range = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range);

        var handler = new TrackingCleanupHandler();
        list.ProducerCleanupReleasedEntries(ref handler);

        Assert.True(accessor.HasFreeSlabs);

        for (var i = 0; i < SmallSlabLength; i++)
        {
            Assert.Null(originalHead.Entries[i].Payload);
            Assert.Equal(0, originalHead.Entries[i].PublishTimeNanoseconds);
        }
    }

    // ═══════════════════════════ CONSUMER SLAB ADVANCEMENT ═══════════════════

    [Fact]
    public void ConsumerSlab_AdvancesMultipleSlabs_WhenFarBehind()
    {
        // Producer races ahead by many slabs. Consumer acquires everything in one shot.
        // The acquire's while loop must advance _consumerSlab through multiple slabs.
        var list = new SlabList<string>(SmallSlabLength);

        var total = SmallSlabLength * 5;
        for (var i = 0; i < total; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(total, range.Count);

        for (var i = 0; i < total; i++)
            Assert.Equal($"e{i}", range[i].Payload);

        list.ConsumerReleaseEntries(in range);

        // Second acquire should be empty — consumer caught up.
        var empty = list.ConsumerAcquireEntries();
        Assert.True(empty.IsEmpty);
    }

    [Fact]
    public void ConsumerSlab_AdvancesMultipleSlabs_AcrossMultipleAcquireCycles()
    {
        // Producer is always ahead. Consumer catches up in small batches, each requiring slab advancement.
        var list = new SlabList<string>(SmallSlabLength);

        // Produce 4 slabs worth.
        var total = SmallSlabLength * 4;
        for (var i = 0; i < total; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        // Consume first half (2 slabs).
        var range1 = list.ConsumerAcquireEntries();
        Assert.Equal(total, range1.Count);
        list.ConsumerReleaseEntries(in range1);

        // Producer produces more.
        for (var i = total; i < total + SmallSlabLength; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range2 = list.ConsumerAcquireEntries();
        Assert.Equal(SmallSlabLength, range2.Count);
        Assert.Equal($"e{total}", range2[0].Payload);
    }

    // ═══════════════════════════ INTERLEAVING PATTERNS ═══════════════════════

    [Fact]
    public void ProduceAcquireProduceMore_ThenRelease_ThenAcquireAgain()
    {
        // Consumer acquires a batch but doesn't release. Producer appends more. Consumer releases
        // the first batch, then acquires the new entries.
        var list = new SlabList<string>(SmallSlabLength);

        for (var i = 0; i < SmallSlabLength + 2; i++)
            list.ProducerAppendEntry(MakeEntry($"batch1-{i}"));

        var range1 = list.ConsumerAcquireEntries();
        Assert.Equal(SmallSlabLength + 2, range1.Count);

        // Producer appends more while consumer holds range1.
        for (var i = 0; i < SmallSlabLength; i++)
            list.ProducerAppendEntry(MakeEntry($"batch2-{i}"));

        // Consumer releases batch 1.
        list.ConsumerReleaseEntries(in range1);

        // Consumer acquires batch 2.
        var range2 = list.ConsumerAcquireEntries();
        Assert.Equal(SmallSlabLength, range2.Count);
        for (var i = 0; i < SmallSlabLength; i++)
            Assert.Equal($"batch2-{i}", range2[i].Payload);
    }

    [Fact]
    public void Cleanup_DeferredAcrossManySlabs()
    {
        // Producer produces multiple slabs. Consumer consumes and releases all. Cleanup is called
        // once and should recycle all completed slabs in a single pass.
        var list = new SlabList<string>(SmallSlabLength);
        var accessor = list.GetTestAccessor();

        var total = (SmallSlabLength * 6) + 2;
        for (var i = 0; i < total; i++)
            list.ProducerAppendEntry(MakeEntry($"e{i}"));

        var range = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range);

        var handler = new TrackingCleanupHandler();
        list.ProducerCleanupReleasedEntries(ref handler);

        Assert.Equal(total, handler.CleanedEntries.Count);
        Assert.Equal(6, accessor.FreeSlabCount);
    }

    [Fact]
    public void Cleanup_Delayed_ProducerAppendsContinue()
    {
        // Producer appends, consumer releases, but cleanup is NOT called for several rounds.
        // When cleanup finally runs, it processes everything accumulated.
        var list = new SlabList<string>(SmallSlabLength);
        var accessor = list.GetTestAccessor();

        for (var round = 0; round < 5; round++)
        {
            for (var i = 0; i < SmallSlabLength; i++)
                list.ProducerAppendEntry(MakeEntry($"r{round}-{i}"));

            var range = list.ConsumerAcquireEntries();
            list.ConsumerReleaseEntries(in range);
        }

        Assert.Equal(0, accessor.CleanupPosition);

        var handler = new TrackingCleanupHandler();
        list.ProducerCleanupReleasedEntries(ref handler);

        Assert.Equal(SmallSlabLength * 5, handler.CleanedEntries.Count);
        Assert.Equal(SmallSlabLength * 5, accessor.CleanupPosition);
        Assert.True(accessor.FreeSlabCount > 0);
    }

    // ═══════════════════════════ STRESS TESTS ════════════════════════════════

    [Fact]
    public void Stress_HighVolume_SingleThreaded_VaryingBatchSizes()
    {
        var list = new SlabList<string>(SmallSlabLength);
        var handler = new TrackingCleanupHandler();
        var batchSizes = new[] { 1, 2, 3, 4, 5, 7, 8, 9, 13, 16, 17, 25, 32, 33 };
        var totalProduced = 0L;

        foreach (var batchSize in batchSizes)
        {
            for (var i = 0; i < batchSize; i++)
            {
                list.ProducerAppendEntry(MakeEntry($"e{totalProduced}", totalProduced));
                totalProduced++;
            }

            var range = list.ConsumerAcquireEntries();
            Assert.Equal(batchSize, range.Count);

            var idx = 0;
            foreach (ref readonly var entry in range)
            {
                Assert.Equal(totalProduced - batchSize + idx, entry.PublishTimeNanoseconds);
                idx++;
            }

            Assert.Equal(batchSize, idx);
            list.ConsumerReleaseEntries(in range);
            list.ProducerCleanupReleasedEntries(ref handler);
        }

        Assert.Equal(totalProduced, handler.CleanedEntries.Count);

        for (var i = 0; i < totalProduced; i++)
            Assert.Equal(i, handler.CleanedEntries[i].Position);
    }

    [Fact]
    public void Stress_HighVolume_SingleThreaded_ConsumerLags()
    {
        // Producer runs far ahead, consumer catches up in large batches.
        var list = new SlabList<string>(SmallSlabLength);
        var handler = new TrackingCleanupHandler();
        var totalConsumed = 0L;
        var totalProduced = 0L;
        var target = 1000L;

        while (totalConsumed < target)
        {
            var produceBatch = Math.Min(37, target - totalProduced);
            for (var i = 0; i < produceBatch; i++)
            {
                list.ProducerAppendEntry(MakeEntry($"e{totalProduced}", totalProduced));
                totalProduced++;
            }

            var range = list.ConsumerAcquireEntries();
            Assert.Equal(totalProduced - totalConsumed, range.Count);

            for (var i = 0L; i < range.Count; i++)
                Assert.Equal(totalConsumed + i, range[i].PublishTimeNanoseconds);

            totalConsumed += range.Count;
            list.ConsumerReleaseEntries(in range);
            list.ProducerCleanupReleasedEntries(ref handler);
        }

        Assert.Equal(target, totalConsumed);
        Assert.Equal(target, handler.CleanedEntries.Count);
    }

    [Fact]
    public void Stress_MultiThreaded_SPSC()
    {
        const int EntryCount = 100_000;
        var list = new SlabList<long>(SmallSlabLength);
        var consumed = new long[EntryCount];
        var consumerDone = new ManualResetEventSlim(false);
        var producerDone = new ManualResetEventSlim(false);

        var producerThread = new Thread(() =>
        {
            for (var i = 0L; i < EntryCount; i++)
                list.ProducerAppendEntry(new PipelineEntry<long> { Payload = i, PublishTimeNanoseconds = i });

            producerDone.Set();
        });

        var consumerThread = new Thread(() =>
        {
            var totalConsumed = 0L;

            while (totalConsumed < EntryCount)
            {
                var range = list.ConsumerAcquireEntries();
                if (range.IsEmpty)
                {
                    Thread.SpinWait(10);
                    continue;
                }

                foreach (ref readonly var entry in range)
                {
                    consumed[totalConsumed] = entry.Payload;
                    totalConsumed++;
                }

                list.ConsumerReleaseEntries(in range);
            }

            consumerDone.Set();
        });

        producerThread.Start();
        consumerThread.Start();

        producerDone.Wait(TestContext.Current.CancellationToken);
        consumerDone.Wait(TestContext.Current.CancellationToken);

        for (var i = 0L; i < EntryCount; i++)
            Assert.Equal(i, consumed[i]);
    }

    [Fact]
    public void Stress_MultiThreaded_SPSC_WithCleanup()
    {
        const int EntryCount = 100_000;
        var list = new SlabList<long>(SmallSlabLength);
        var consumed = new long[EntryCount];
        var allDone = new ManualResetEventSlim(false);

        long producerCleanedCount = 0;

        var producerThread = new Thread(() =>
        {
            var handler = new CountingCleanupHandler();
            for (var i = 0L; i < EntryCount; i++)
            {
                list.ProducerAppendEntry(new PipelineEntry<long> { Payload = i, PublishTimeNanoseconds = i });

                if (i % 100 == 0)
                    list.ProducerCleanupReleasedEntries(ref handler);
            }

            while (Volatile.Read(ref producerCleanedCount) < EntryCount)
            {
                list.ProducerCleanupReleasedEntries(ref handler);
                Volatile.Write(ref producerCleanedCount, handler.Count);
                if (handler.Count < EntryCount)
                    Thread.SpinWait(100);
            }
        });

        var consumerThread = new Thread(() =>
        {
            var totalConsumed = 0L;

            while (totalConsumed < EntryCount)
            {
                var range = list.ConsumerAcquireEntries();
                if (range.IsEmpty)
                {
                    Thread.SpinWait(10);
                    continue;
                }

                foreach (ref readonly var entry in range)
                {
                    consumed[totalConsumed] = entry.Payload;
                    totalConsumed++;
                }

                list.ConsumerReleaseEntries(in range);
            }

            allDone.Set();
        });

        producerThread.Start();
        consumerThread.Start();

        allDone.Wait(TestContext.Current.CancellationToken);
        producerThread.Join();

        for (var i = 0L; i < EntryCount; i++)
            Assert.Equal(i, consumed[i]);

        Assert.Equal(EntryCount, Volatile.Read(ref producerCleanedCount));
    }

    [Fact]
    public void Stress_MultiThreaded_SPSC_SmallBatches_WithCleanup()
    {
        // Producer appends in small batches with cleanup between each.
        // Consumer processes entries one acquire at a time.
        const int TotalEntries = 50_000;
        var list = new SlabList<long>(SmallSlabLength);
        var consumed = new long[TotalEntries];
        var consumerDone = new ManualResetEventSlim(false);

        long totalCleaned = 0;

        var producerThread = new Thread(() =>
        {
            var handler = new CountingCleanupHandler();
            var produced = 0;
            while (produced < TotalEntries)
            {
                var batchSize = Math.Min((produced % 7) + 1, TotalEntries - produced);
                for (var i = 0; i < batchSize; i++)
                {
                    list.ProducerAppendEntry(new PipelineEntry<long> { Payload = produced, PublishTimeNanoseconds = produced });
                    produced++;
                }

                list.ProducerCleanupReleasedEntries(ref handler);
            }

            while (handler.Count < TotalEntries)
            {
                list.ProducerCleanupReleasedEntries(ref handler);
                Thread.SpinWait(100);
            }

            Volatile.Write(ref totalCleaned, handler.Count);
        });

        var consumerThread = new Thread(() =>
        {
            var idx = 0L;
            while (idx < TotalEntries)
            {
                var range = list.ConsumerAcquireEntries();
                if (range.IsEmpty)
                {
                    Thread.SpinWait(10);
                    continue;
                }

                foreach (ref readonly var entry in range)
                {
                    consumed[idx] = entry.Payload;
                    idx++;
                }

                list.ConsumerReleaseEntries(in range);
            }

            consumerDone.Set();
        });

        producerThread.Start();
        consumerThread.Start();

        consumerDone.Wait(TestContext.Current.CancellationToken);
        producerThread.Join();

        for (var i = 0; i < TotalEntries; i++)
            Assert.Equal(i, consumed[i]);

        Assert.Equal(TotalEntries, Volatile.Read(ref totalCleaned));
    }

    private struct CountingCleanupHandler : IEntryCleanupHandler<long>
    {
        public long _count;

        public readonly long Count => _count;

        public void HandleEntry(long position, in PipelineEntry<long> entry) =>
            _count++;
    }
}
