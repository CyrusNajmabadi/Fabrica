using Fabrica.Core.Threading.Queues;
using Xunit;

namespace Fabrica.Core.Tests.Collections;

public class InjectionQueueTests
{
    // ═══════════════════════════ EMPTY QUEUE ═════════════════════════════════

    [Fact]
    public void NewQueue_IsEmpty()
    {
        var queue = new InjectionQueue<string>();
        Assert.True(queue.IsEmpty);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void TryDequeue_OnEmpty_ReturnsFalse()
    {
        var queue = new InjectionQueue<string>();
        Assert.False(queue.TryDequeue(out var item));
        Assert.Null(item);
    }

    // ═══════════════════════════ ENQUEUE / DEQUEUE ═══════════════════════════

    [Fact]
    public void Enqueue_SingleItem_DequeueReturnsIt()
    {
        var queue = new InjectionQueue<string>();
        queue.Enqueue("a");

        Assert.Equal(1, queue.Count);
        Assert.False(queue.IsEmpty);

        Assert.True(queue.TryDequeue(out var item));
        Assert.Equal("a", item);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void Enqueue_MultipleItems_DequeuedInLifoOrder()
    {
        var queue = new InjectionQueue<string>();
        queue.Enqueue("a");
        queue.Enqueue("b");
        queue.Enqueue("c");

        Assert.Equal(3, queue.Count);

        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal("c", first);

        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal("b", second);

        Assert.True(queue.TryDequeue(out var third));
        Assert.Equal("a", third);

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void TryDequeue_AfterAllDrained_ReturnsFalse()
    {
        var queue = new InjectionQueue<string>();
        queue.Enqueue("a");
        Assert.True(queue.TryDequeue(out _));
        Assert.False(queue.TryDequeue(out _));
    }

    // ═══════════════════════════ COUNT ═══════════════════════════════════════

    [Fact]
    public void Count_TracksEnqueueAndDequeue()
    {
        var queue = new InjectionQueue<int>();

        for (var i = 0; i < 10; i++)
        {
            queue.Enqueue(i);
            Assert.Equal(i + 1, queue.Count);
        }

        for (var i = 9; i >= 0; i--)
        {
            Assert.True(queue.TryDequeue(out _));
            Assert.Equal(i, queue.Count);
        }
    }

    // ═══════════════════════════ INTERLEAVED ═════════════════════════════════

    [Fact]
    public void Interleaved_EnqueueDequeue_WorksCorrectly()
    {
        var queue = new InjectionQueue<int>();

        queue.Enqueue(1);
        queue.Enqueue(2);
        Assert.True(queue.TryDequeue(out var item));
        Assert.Equal(2, item);

        queue.Enqueue(3);
        Assert.True(queue.TryDequeue(out item));
        Assert.Equal(3, item);

        Assert.True(queue.TryDequeue(out item));
        Assert.Equal(1, item);

        Assert.True(queue.IsEmpty);
    }

    // ═══════════════════════════ DRAIN TO LIST ═══════════════════════════════

    [Fact]
    public void DrainToList_ReturnsAllItemsInLifoOrder()
    {
        var queue = new InjectionQueue<string>();
        queue.Enqueue("a");
        queue.Enqueue("b");
        queue.Enqueue("c");

        var drained = queue.GetTestAccessor().DrainToList();
        Assert.Equal(["c", "b", "a"], drained);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void DrainToList_OnEmpty_ReturnsEmptyList()
    {
        var queue = new InjectionQueue<string>();
        var drained = queue.GetTestAccessor().DrainToList();
        Assert.Empty(drained);
    }

    // ═══════════════════════════ CONCURRENT ═════════════════════════════════

    [Theory]
    [InlineData(2, 1_000)]
    [InlineData(4, 5_000)]
    [InlineData(8, 10_000)]
    public void Stress_ConcurrentEnqueueDequeue_NoItemsLost(int threadCount, int itemsPerThread)
    {
        var queue = new InjectionQueue<int>();
        var totalItems = threadCount * itemsPerThread;
        var dequeued = new System.Collections.Concurrent.ConcurrentBag<int>();
        var barrier = new Barrier(threadCount);

        var threads = new Thread[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            var threadId = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();

                for (var i = 0; i < itemsPerThread; i++)
                {
                    var value = (threadId * itemsPerThread) + i;
                    queue.Enqueue(value);

                    if (i % 2 == 0 && queue.TryDequeue(out var item))
                        dequeued.Add(item);
                }
            });
            threads[t].Start();
        }

        foreach (var t in threads)
            t.Join();

        while (queue.TryDequeue(out var remaining))
            dequeued.Add(remaining);

        Assert.True(queue.IsEmpty);

        var all = new HashSet<int>(dequeued);
        Assert.Equal(totalItems, all.Count);
        for (var i = 0; i < totalItems; i++)
            Assert.Contains(i, all);
    }

    // ═══════════════════════════ STRUCT COPY SAFETY ══════════════════════════

    [Fact]
    public void StructCopy_SharesSameBackingState()
    {
        var original = new InjectionQueue<string>();
        var copy = original;

        original.Enqueue("from-original");
        Assert.True(copy.TryDequeue(out var item));
        Assert.Equal("from-original", item);

        copy.Enqueue("from-copy");
        Assert.True(original.TryDequeue(out item));
        Assert.Equal("from-copy", item);
    }
}
