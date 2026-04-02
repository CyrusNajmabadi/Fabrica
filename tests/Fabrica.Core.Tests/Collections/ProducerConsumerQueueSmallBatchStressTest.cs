using Fabrica.Core.Collections;
using Xunit;

namespace Fabrica.Core.Tests.Collections;

[Trait("Category", "Stress")]
public class ProducerConsumerQueueSmallBatchStressTest
{
    private const int SmallSlabLength = 4;

    [Fact]
    public void Stress_MultiThreaded_SPSC_SmallBatches_WithCleanup()
    {
        const int TotalEntries = 50_000;
        var queue = new ProducerConsumerQueue<long>(SmallSlabLength);
        var consumed = new long[TotalEntries];
        var consumerDone = new ManualResetEventSlim(false);

        long totalCleaned = 0;

        var producerThread = new Thread(() =>
        {
            var handler = new LongCountingCleanupHandler();
            var produced = 0;
            while (produced < TotalEntries)
            {
                var batchSize = Math.Min((produced % 7) + 1, TotalEntries - produced);
                for (var i = 0; i < batchSize; i++)
                {
                    queue.ProducerAppend(produced);
                    produced++;
                }

                queue.ProducerCleanup(ref handler);
            }

            while (handler.Count < TotalEntries)
            {
                queue.ProducerCleanup(ref handler);
                Thread.SpinWait(100);
            }

            Volatile.Write(ref totalCleaned, handler.Count);
        });

        var consumerThread = new Thread(() =>
        {
            var idx = 0L;
            while (idx < TotalEntries)
            {
                var segment = queue.ConsumerAcquire();
                if (segment.IsEmpty)
                {
                    Thread.SpinWait(10);
                    continue;
                }

                foreach (ref readonly var item in segment)
                {
                    consumed[idx] = item;
                    idx++;
                }

                queue.ConsumerAdvance(segment.Count);
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

    private struct LongCountingCleanupHandler : ProducerConsumerQueue<long>.ICleanupHandler
    {
        public long _count;

        public readonly long Count => _count;

        public void HandleCleanup(long position, in long item) =>
            _count++;
    }
}
