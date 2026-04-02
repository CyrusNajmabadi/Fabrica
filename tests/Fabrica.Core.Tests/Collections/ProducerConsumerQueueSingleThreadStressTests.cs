using Fabrica.Core.Collections;
using Xunit;

namespace Fabrica.Core.Tests.Collections;

[Trait("Category", "Stress")]
public class ProducerConsumerQueueSingleThreadStressTests
{
    private const int SmallSlabLength = 4;

    private readonly struct TrackingCleanupHandler : ProducerConsumerQueue<string>.ICleanupHandler
    {
        public List<(long Position, string? Item)> CleanedEntries { get; }

        public TrackingCleanupHandler() => this.CleanedEntries = [];

        public readonly void HandleCleanup(long position, in string item) =>
            this.CleanedEntries.Add((position, item));
    }

    [Fact]
    public void Stress_HighVolume_SingleThreaded_VaryingBatchSizes()
    {
        var queue = new ProducerConsumerQueue<string>(SmallSlabLength);
        var handler = new TrackingCleanupHandler();
        var batchSizes = new[] { 1, 2, 3, 4, 5, 7, 8, 9, 13, 16, 17, 25, 32, 33 };
        var totalProduced = 0;

        foreach (var batchSize in batchSizes)
        {
            for (var i = 0; i < batchSize; i++)
            {
                queue.ProducerAppend($"e{totalProduced}");
                totalProduced++;
            }

            var segment = queue.ConsumerAcquire();
            Assert.Equal(batchSize, segment.Count);

            var idx = 0;
            foreach (ref readonly var item in segment)
            {
                Assert.Equal($"e{totalProduced - batchSize + idx}", item);
                idx++;
            }

            Assert.Equal(batchSize, idx);
            queue.ConsumerAdvance(segment.Count);
            queue.ProducerCleanup(ref handler);
        }

        Assert.Equal(totalProduced, handler.CleanedEntries.Count);

        for (var i = 0; i < totalProduced; i++)
            Assert.Equal(i, handler.CleanedEntries[i].Position);
    }

    [Fact]
    public void Stress_HighVolume_SingleThreaded_ConsumerLags()
    {
        var queue = new ProducerConsumerQueue<long>(SmallSlabLength);
        var handler = new LongCountingCleanupHandler();
        var totalConsumed = 0L;
        var totalProduced = 0L;
        var target = 1000L;

        while (totalConsumed < target)
        {
            var produceBatch = Math.Min(37, target - totalProduced);
            for (var i = 0; i < produceBatch; i++)
            {
                queue.ProducerAppend(totalProduced);
                totalProduced++;
            }

            var segment = queue.ConsumerAcquire();
            Assert.Equal(totalProduced - totalConsumed, segment.Count);

            for (var i = 0; i < (int)segment.Count; i++)
                Assert.Equal(totalConsumed + i, segment[i]);

            totalConsumed += segment.Count;
            queue.ConsumerAdvance(segment.Count);
            queue.ProducerCleanup(ref handler);
        }

        Assert.Equal(target, totalConsumed);
        Assert.Equal(target, handler.Count);
    }

    private struct LongCountingCleanupHandler : ProducerConsumerQueue<long>.ICleanupHandler
    {
        public long _count;

        public readonly long Count => _count;

        public void HandleCleanup(long position, in long item) =>
            _count++;
    }
}
