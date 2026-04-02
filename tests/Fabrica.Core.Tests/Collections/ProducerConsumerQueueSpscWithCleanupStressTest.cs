using Fabrica.Core.Collections;
using Xunit;

namespace Fabrica.Core.Tests.Collections;

[Trait("Category", "Stress")]
public class ProducerConsumerQueueSpscWithCleanupStressTest
{
    private const int SmallSlabLength = 4;

    [Fact]
    public void Stress_MultiThreaded_SPSC_WithCleanup()
    {
        const int EntryCount = 100_000;
        var queue = new ProducerConsumerQueue<long>(SmallSlabLength);
        var consumed = new long[EntryCount];
        var allDone = new ManualResetEventSlim(false);

        long producerCleanedCount = 0;

        var producerThread = new Thread(() =>
        {
            var handler = new LongCountingCleanupHandler();
            for (var i = 0L; i < EntryCount; i++)
            {
                queue.ProducerAppend(i);

                if (i % 100 == 0)
                    queue.ProducerCleanup(ref handler);
            }

            while (Volatile.Read(ref producerCleanedCount) < EntryCount)
            {
                queue.ProducerCleanup(ref handler);
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
                var segment = queue.ConsumerAcquire();
                if (segment.IsEmpty)
                {
                    Thread.SpinWait(10);
                    continue;
                }

                foreach (ref readonly var item in segment)
                {
                    consumed[totalConsumed] = item;
                    totalConsumed++;
                }

                queue.ConsumerRelease(in segment);
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

    private struct LongCountingCleanupHandler : ProducerConsumerQueue<long>.ICleanupHandler
    {
        public long _count;

        public readonly long Count => _count;

        public void HandleCleanup(long position, in long item) =>
            _count++;
    }
}
