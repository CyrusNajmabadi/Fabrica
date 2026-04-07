using Fabrica.Core.Threading.Queues;
using Xunit;

namespace Fabrica.Core.Tests.Collections;

[Trait("Category", "Stress")]
public class ProducerConsumerQueueSpscStressTest
{
    private const int SmallSlabLength = 4;

    [Fact]
    public void Stress_MultiThreaded_SPSC()
    {
        const int EntryCount = 100_000;
        var queue = new ProducerConsumerQueue<long>(SmallSlabLength);
        var consumed = new long[EntryCount];
        var consumerDone = new ManualResetEventSlim(false);
        var producerDone = new ManualResetEventSlim(false);

        var producerThread = new Thread(() =>
        {
            for (var i = 0L; i < EntryCount; i++)
                queue.ProducerAppend(i);

            producerDone.Set();
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

                queue.ConsumerAdvance(segment.Count);
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
}
