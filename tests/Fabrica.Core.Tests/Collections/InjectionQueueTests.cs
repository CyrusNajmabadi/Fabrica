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
    public void TryDequeue_OnEmpty_ReturnsNull()
    {
        var queue = new InjectionQueue<string>();
        Assert.Null(queue.TryDequeue());
    }

    // ═══════════════════════════ ENQUEUE / DEQUEUE ═══════════════════════════

    [Fact]
    public void Enqueue_SingleItem_DequeueReturnsIt()
    {
        var queue = new InjectionQueue<string>();
        queue.Enqueue("a");

        Assert.Equal(1, queue.Count);
        Assert.False(queue.IsEmpty);

        Assert.Equal("a", queue.TryDequeue());
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

        Assert.Equal("c", queue.TryDequeue());
        Assert.Equal("b", queue.TryDequeue());
        Assert.Equal("a", queue.TryDequeue());

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void TryDequeue_AfterAllDrained_ReturnsFalse()
    {
        var queue = new InjectionQueue<string>();
        queue.Enqueue("a");
        Assert.NotNull(queue.TryDequeue());
        Assert.Null(queue.TryDequeue());
    }

    // ═══════════════════════════ COUNT ═══════════════════════════════════════

    [Fact]
    public void Count_TracksEnqueueAndDequeue()
    {
        var queue = new InjectionQueue<string>();

        for (var i = 0; i < 10; i++)
        {
            queue.Enqueue(i.ToString());
            Assert.Equal(i + 1, queue.Count);
        }

        for (var i = 9; i >= 0; i--)
        {
            Assert.NotNull(queue.TryDequeue());
            Assert.Equal(i, queue.Count);
        }
    }

    // ═══════════════════════════ INTERLEAVED ═════════════════════════════════

    [Fact]
    public void Interleaved_EnqueueDequeue_WorksCorrectly()
    {
        var queue = new InjectionQueue<string>();

        queue.Enqueue("1");
        queue.Enqueue("2");
        Assert.Equal("2", queue.TryDequeue());

        queue.Enqueue("3");
        Assert.Equal("3", queue.TryDequeue());

        Assert.Equal("1", queue.TryDequeue());

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
        var queue = new InjectionQueue<string>();
        var totalItems = threadCount * itemsPerThread;
        var dequeued = new System.Collections.Concurrent.ConcurrentBag<string>();
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
                    var value = ((threadId * itemsPerThread) + i).ToString();
                    queue.Enqueue(value);

                    if (i % 2 == 0)
                    {
                        var item = queue.TryDequeue();
                        if (item != null) dequeued.Add(item);
                    }
                }
            });
            threads[t].Start();
        }

        foreach (var t in threads)
            t.Join();

        for (var remaining = queue.TryDequeue(); remaining != null; remaining = queue.TryDequeue())
            dequeued.Add(remaining);

        Assert.True(queue.IsEmpty);

        var all = new HashSet<string>(dequeued);
        Assert.Equal(totalItems, all.Count);
        for (var i = 0; i < totalItems; i++)
            Assert.Contains(i.ToString(), all);
    }

    // ═══════════════════════════ STRUCT COPY SAFETY ══════════════════════════

    [Fact]
    public void StructCopy_DoesNotShareState()
    {
        var original = new InjectionQueue<string>();
        var copy = original;

        original.Enqueue("from-original");
        Assert.Null(copy.TryDequeue());
        Assert.Equal("from-original", original.TryDequeue());
    }
}
