using Fabrica.Core.Collections;
using Xunit;

namespace Fabrica.Pipeline.Tests.Pipeline;

public class SlabListTests
{
    // ═══════════════════════════ TEST HELPERS ════════════════════════════════

    private static PipelineEntry<string> MakeEntry(string payload, long time = 0) =>
        new() { Payload = payload, PublishTimeNanoseconds = time };

    private readonly struct TrackingCleanupHandler : IEntryCleanupHandler<string>
    {
        public List<(long Position, string Payload)> CleanedEntries { get; }

        public TrackingCleanupHandler() => this.CleanedEntries = [];

        public readonly void HandleEntry(long position, in PipelineEntry<string> entry) =>
            this.CleanedEntries.Add((position, entry.Payload));
    }

    // ═══════════════════════════ APPEND + ACQUIRE ════════════════════════════

    [Fact]
    public void ConsumerAcquireEntries_ReturnsEmpty_WhenNothingProduced()
    {
        var list = new SlabList<string>();
        var range = list.ConsumerAcquireEntries();
        Assert.True(range.IsEmpty);
        Assert.Equal(0, range.Count);
    }

    [Fact]
    public void ProducerAppendEntry_SingleEntry_ConsumerAcquiresIt()
    {
        var list = new SlabList<string>();
        list.ProducerAppendEntry(MakeEntry("tick-0", 100));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(1, range.Count);
        Assert.Equal("tick-0", range[0].Payload);
        Assert.Equal(100, range[0].PublishTimeNanoseconds);
    }

    [Fact]
    public void ProducerAppendEntry_MultipleEntries_ConsumerAcquiresAll()
    {
        var list = new SlabList<string>();
        for (var i = 0; i < 5; i++)
            list.ProducerAppendEntry(MakeEntry($"tick-{i}", i * 25));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(5, range.Count);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal($"tick-{i}", range[i].Payload);
            Assert.Equal(i * 25, range[i].PublishTimeNanoseconds);
        }
    }

    [Fact]
    public void ConsumerAcquireEntries_ReturnsEmpty_AfterConsumerCaughtUp()
    {
        var list = new SlabList<string>();
        list.ProducerAppendEntry(MakeEntry("tick-0"));

        var range = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range);

        var second = list.ConsumerAcquireEntries();
        Assert.True(second.IsEmpty);
    }

    // ═══════════════════════════ ACQUIRE / RELEASE CYCLE ════════════════════

    [Fact]
    public void MultipleAcquireReleaseCycles_ReturnsOnlyNewEntries()
    {
        var list = new SlabList<string>();

        list.ProducerAppendEntry(MakeEntry("a"));
        list.ProducerAppendEntry(MakeEntry("b"));
        var range1 = list.ConsumerAcquireEntries();
        Assert.Equal(2, range1.Count);
        list.ConsumerReleaseEntries(in range1);

        list.ProducerAppendEntry(MakeEntry("c"));
        var range2 = list.ConsumerAcquireEntries();
        Assert.Equal(1, range2.Count);
        Assert.Equal("c", range2[0].Payload);
        list.ConsumerReleaseEntries(in range2);
    }

    [Fact]
    public void ConsumerReleaseEntries_WithEmptyRange_IsNoOp()
    {
        var list = new SlabList<string>();
        var empty = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in empty);

        list.ProducerAppendEntry(MakeEntry("a"));
        var range = list.ConsumerAcquireEntries();
        Assert.Equal(1, range.Count);
        Assert.Equal("a", range[0].Payload);
    }

    [Fact]
    public void ProducerPosition_AdvancesWithEachAppend()
    {
        var list = new SlabList<string>();
        var accessor = list.GetTestAccessor();

        Assert.Equal(0, accessor.ProducerPosition);
        list.ProducerAppendEntry(MakeEntry("a"));
        Assert.Equal(1, accessor.ProducerPosition);
        list.ProducerAppendEntry(MakeEntry("b"));
        Assert.Equal(2, accessor.ProducerPosition);
    }

    [Fact]
    public void ConsumerPosition_AdvancesAfterRelease()
    {
        var list = new SlabList<string>();
        var accessor = list.GetTestAccessor();

        list.ProducerAppendEntry(MakeEntry("a"));
        list.ProducerAppendEntry(MakeEntry("b"));

        Assert.Equal(0, accessor.ConsumerPosition);
        var range = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range);
        Assert.Equal(2, accessor.ConsumerPosition);
    }

    // ═══════════════════════════ SLAB BOUNDARY ══════════════════════════════

    [Fact]
    public void AppendAcross_SlabBoundary_CreatesNewSlab()
    {
        var list = new SlabList<string>();
        var accessor = list.GetTestAccessor();
        var slabLength = SlabSizeHelper<string>.SlabLength;

        for (var i = 0; i < slabLength + 1; i++)
            list.ProducerAppendEntry(MakeEntry($"tick-{i}"));

        Assert.NotSame(accessor.HeadSlab, accessor.TailSlab);
        Assert.Equal(slabLength + 1, accessor.ProducerPosition);
    }

    [Fact]
    public void ConsumerAcquiresEntries_AcrossSlabBoundary()
    {
        var list = new SlabList<string>();
        var slabLength = SlabSizeHelper<string>.SlabLength;
        var totalEntries = slabLength + 10;

        for (var i = 0; i < totalEntries; i++)
            list.ProducerAppendEntry(MakeEntry($"tick-{i}", i));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(totalEntries, range.Count);

        for (var i = 0; i < totalEntries; i++)
        {
            Assert.Equal($"tick-{i}", range[i].Payload);
            Assert.Equal(i, range[i].PublishTimeNanoseconds);
        }
    }

    [Fact]
    public void Enumerator_CrossesSlabBoundary()
    {
        var list = new SlabList<string>();
        var slabLength = SlabSizeHelper<string>.SlabLength;
        var totalEntries = slabLength + 10;

        for (var i = 0; i < totalEntries; i++)
            list.ProducerAppendEntry(MakeEntry($"tick-{i}"));

        var range = list.ConsumerAcquireEntries();
        var collected = new List<string>();
        foreach (ref readonly var entry in range)
            collected.Add(entry.Payload);

        Assert.Equal(totalEntries, collected.Count);
        for (var i = 0; i < totalEntries; i++)
            Assert.Equal($"tick-{i}", collected[i]);
    }

    [Fact]
    public void ConsumerRelease_AtExactSlabBoundary_ThenAcquireMore()
    {
        var list = new SlabList<string>();
        var slabLength = SlabSizeHelper<string>.SlabLength;

        for (var i = 0; i < slabLength; i++)
            list.ProducerAppendEntry(MakeEntry($"tick-{i}"));

        var range1 = list.ConsumerAcquireEntries();
        Assert.Equal(slabLength, range1.Count);
        list.ConsumerReleaseEntries(in range1);

        for (var i = slabLength; i < slabLength + 5; i++)
            list.ProducerAppendEntry(MakeEntry($"tick-{i}"));

        var range2 = list.ConsumerAcquireEntries();
        Assert.Equal(5, range2.Count);
        Assert.Equal($"tick-{slabLength}", range2[0].Payload);
        Assert.Equal($"tick-{slabLength + 4}", range2[4].Payload);
    }

    // ═══════════════════════════ CLEANUP ═════════════════════════════════════

    [Fact]
    public void ProducerCleanupReleasedEntries_CallsHandlerForEachEntry()
    {
        var list = new SlabList<string>();
        list.ProducerAppendEntry(MakeEntry("a", 10));
        list.ProducerAppendEntry(MakeEntry("b", 20));

        var range = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range);

        var handler = new TrackingCleanupHandler();
        list.ProducerCleanupReleasedEntries(ref handler);

        Assert.Equal(2, handler.CleanedEntries.Count);
        Assert.Equal((0, "a"), handler.CleanedEntries[0]);
        Assert.Equal((1, "b"), handler.CleanedEntries[1]);
    }

    [Fact]
    public void ProducerCleanupReleasedEntries_ClearsSlots()
    {
        var list = new SlabList<string>();
        var accessor = list.GetTestAccessor();
        list.ProducerAppendEntry(MakeEntry("a"));

        var range = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range);

        var handler = new TrackingCleanupHandler();
        list.ProducerCleanupReleasedEntries(ref handler);

        Assert.Null(accessor.HeadSlab.Entries[0].Payload);
        Assert.Equal(0, accessor.HeadSlab.Entries[0].PublishTimeNanoseconds);
    }

    [Fact]
    public void ProducerCleanupReleasedEntries_AdvancesCleanupPosition()
    {
        var list = new SlabList<string>();
        var accessor = list.GetTestAccessor();

        list.ProducerAppendEntry(MakeEntry("a"));
        list.ProducerAppendEntry(MakeEntry("b"));

        var range = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range);

        Assert.Equal(0, accessor.CleanupPosition);
        var handler = new TrackingCleanupHandler();
        list.ProducerCleanupReleasedEntries(ref handler);
        Assert.Equal(2, accessor.CleanupPosition);
    }

    [Fact]
    public void ProducerCleanupReleasedEntries_DoesNotCleanBeyondConsumerPosition()
    {
        var list = new SlabList<string>();
        list.ProducerAppendEntry(MakeEntry("a"));
        list.ProducerAppendEntry(MakeEntry("b"));

        var range = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range);

        list.ProducerAppendEntry(MakeEntry("c"));

        var handler = new TrackingCleanupHandler();
        list.ProducerCleanupReleasedEntries(ref handler);

        Assert.Equal(2, handler.CleanedEntries.Count);
    }

    [Fact]
    public void ProducerCleanupReleasedEntries_RecyclesFullSlabs()
    {
        var list = new SlabList<string>();
        var accessor = list.GetTestAccessor();
        var slabLength = SlabSizeHelper<string>.SlabLength;

        for (var i = 0; i < slabLength + 1; i++)
            list.ProducerAppendEntry(MakeEntry($"tick-{i}"));

        var range = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range);

        Assert.False(accessor.HasFreeSlabs);
        var handler = new TrackingCleanupHandler();
        list.ProducerCleanupReleasedEntries(ref handler);

        Assert.True(accessor.HasFreeSlabs);
        Assert.Equal(slabLength + 1, handler.CleanedEntries.Count);
    }

    [Fact]
    public void RecycledSlab_IsReusedForNewAppends()
    {
        var list = new SlabList<string>();
        var accessor = list.GetTestAccessor();
        var slabLength = SlabSizeHelper<string>.SlabLength;

        for (var i = 0; i < slabLength + 1; i++)
            list.ProducerAppendEntry(MakeEntry($"round1-{i}"));

        var range = list.ConsumerAcquireEntries();
        list.ConsumerReleaseEntries(in range);

        var handler = new TrackingCleanupHandler();
        list.ProducerCleanupReleasedEntries(ref handler);
        Assert.True(accessor.HasFreeSlabs);

        for (var i = 0; i < slabLength; i++)
            list.ProducerAppendEntry(MakeEntry($"round2-{i}"));

        Assert.False(accessor.HasFreeSlabs);

        var range2 = list.ConsumerAcquireEntries();
        Assert.Equal(slabLength, range2.Count);
        Assert.Equal("round2-0", range2[0].Payload);
    }

    // ═══════════════════════════ RANGE INDEXER ═══════════════════════════════

    [Fact]
    public void SlabRange_Indexer_ThrowsOnNegativeIndex()
    {
        var list = new SlabList<string>();
        list.ProducerAppendEntry(MakeEntry("a"));
        var range = list.ConsumerAcquireEntries();

        try
        {
            _ = range[-1];
            Assert.Fail("Expected ArgumentOutOfRangeException");
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    [Fact]
    public void SlabRange_Indexer_ThrowsOnOutOfBoundsIndex()
    {
        var list = new SlabList<string>();
        list.ProducerAppendEntry(MakeEntry("a"));
        var range = list.ConsumerAcquireEntries();

        try
        {
            _ = range[1];
            Assert.Fail("Expected ArgumentOutOfRangeException");
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    [Fact]
    public void SlabRange_Indexer_RandomAccess_AcrossSlabBoundary()
    {
        var list = new SlabList<string>();
        var slabLength = SlabSizeHelper<string>.SlabLength;

        for (var i = 0; i < slabLength + 5; i++)
            list.ProducerAppendEntry(MakeEntry($"tick-{i}"));

        var range = list.ConsumerAcquireEntries();

        Assert.Equal("tick-0", range[0].Payload);
        Assert.Equal($"tick-{slabLength - 1}", range[slabLength - 1].Payload);
        Assert.Equal($"tick-{slabLength}", range[slabLength].Payload);
        Assert.Equal($"tick-{slabLength + 4}", range[slabLength + 4].Payload);
    }

    // ═══════════════════════════ LARGE VOLUME ════════════════════════════════

    [Fact]
    public void LargeVolume_ManySlabs_NoDataLoss()
    {
        var list = new SlabList<string>();
        var slabLength = SlabSizeHelper<string>.SlabLength;
        var totalEntries = (slabLength * 3) + 42;

        for (var i = 0; i < totalEntries; i++)
            list.ProducerAppendEntry(MakeEntry($"tick-{i}", i));

        var range = list.ConsumerAcquireEntries();
        Assert.Equal(totalEntries, range.Count);

        var index = 0;
        foreach (ref readonly var entry in range)
        {
            Assert.Equal($"tick-{index}", entry.Payload);
            Assert.Equal(index, entry.PublishTimeNanoseconds);
            index++;
        }

        Assert.Equal(totalEntries, index);
    }

    [Fact]
    public void IncrementalProduceConsumeCleanup_OverMultipleSlabs()
    {
        var list = new SlabList<string>();
        var accessor = list.GetTestAccessor();
        var slabLength = SlabSizeHelper<string>.SlabLength;
        var handler = new TrackingCleanupHandler();
        var totalCleaned = 0;

        for (var batch = 0; batch < 5; batch++)
        {
            var batchSize = (slabLength / 2) + 7;
            for (var i = 0; i < batchSize; i++)
                list.ProducerAppendEntry(MakeEntry($"batch{batch}-{i}"));

            var range = list.ConsumerAcquireEntries();
            Assert.Equal(batchSize, range.Count);
            Assert.Equal($"batch{batch}-0", range[0].Payload);
            list.ConsumerReleaseEntries(in range);

            list.ProducerCleanupReleasedEntries(ref handler);
            totalCleaned += batchSize;
            Assert.Equal(totalCleaned, handler.CleanedEntries.Count);
        }

        Assert.Equal(accessor.ProducerPosition, accessor.CleanupPosition);
    }
}
