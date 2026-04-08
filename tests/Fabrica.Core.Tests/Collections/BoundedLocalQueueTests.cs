using System.Collections.Concurrent;
using Fabrica.Core.Threading.Queues;
using Xunit;

namespace Fabrica.Core.Tests.Collections;

public class BoundedLocalQueueTests
{
    private static ConcurrentQueue<T> MakeOverflow<T>() => new();

    // ═══════════════════════════ EMPTY QUEUE ═════════════════════════════════

    [Fact]
    public void NewQueue_IsEmpty()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.True(queue.IsEmpty);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void TryPop_OnEmpty_ReturnsFalse()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.False(queue.TryPop(out var item));
        Assert.Null(item);
    }

    [Fact]
    public void TryStealHalf_OnEmpty_ReturnsFalse()
    {
        var victim = new BoundedLocalQueue<string>(MakeOverflow<string>());
        var thief = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.False(victim.TryStealHalf(thief, out var item));
        Assert.Null(item);
    }

    // ═══════════════════════════ PUSH / POP (LIFO SLOT) ═════════════════════

    [Fact]
    public void Push_SingleItem_PopReturnsIt()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        queue.Push("a");

        Assert.Equal(1, queue.Count);
        Assert.True(queue.TryPop(out var item));
        Assert.Equal("a", item);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void Push_TwoItems_PopReturnsNewestFirst_ThenOldest()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        queue.Push("a");
        queue.Push("b");

        Assert.True(queue.TryPop(out var first));
        Assert.Equal("b", first);

        Assert.True(queue.TryPop(out var second));
        Assert.Equal("a", second);

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void Push_ThreeItems_PopReturnsLifoThenFifo()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        queue.Push("a");
        queue.Push("b");
        queue.Push("c");

        Assert.True(queue.TryPop(out var first));
        Assert.Equal("c", first);

        Assert.True(queue.TryPop(out var second));
        Assert.Equal("a", second);

        Assert.True(queue.TryPop(out var third));
        Assert.Equal("b", third);

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void Pop_AfterAllPopped_ReturnsFalse()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        queue.Push("a");
        Assert.True(queue.TryPop(out _));
        Assert.False(queue.TryPop(out _));
    }

    // ═══════════════════════════ PUSH / STEAL HALF ═══════════════════════════

    [Fact]
    public void Push_SingleItem_StealHalfReturnsIt()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        queue.Push("a");

        var thief = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.True(queue.TryStealHalf(thief, out var item));
        Assert.Equal("a", item);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void Push_TwoItems_StealHalfTakesOldestFromRing()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        queue.Push("a");
        queue.Push("b");

        var thief = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.True(queue.TryStealHalf(thief, out var first));
        Assert.Equal("a", first);

        Assert.True(queue.TryPop(out var second));
        Assert.Equal("b", second);

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void StealHalf_AfterAllStolen_ReturnsFalse()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        queue.Push("a");
        var thief = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.True(queue.TryStealHalf(thief, out _));
        Assert.False(queue.TryStealHalf(thief, out _));
    }

    // ═══════════════════════════ INTERLEAVED POP + STEAL HALF ════════════════

    [Fact]
    public void Push_Three_StealHalfTakesOldest_PopTakesNewest()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        queue.Push("a");
        queue.Push("b");
        queue.Push("c");

        var thief = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.True(queue.TryStealHalf(thief, out var stolen));

        Assert.True(queue.TryPop(out var popped));
        Assert.Equal("c", popped);

        var all = new HashSet<string> { stolen, popped };
        while (thief.TryPop(out var t))
            Assert.True(all.Add(t));
        while (queue.TryPop(out var q))
            Assert.True(all.Add(q));

        Assert.Equal(3, all.Count);
        Assert.Contains("a", all);
        Assert.Contains("b", all);
        Assert.Contains("c", all);
    }

    // ═══════════════════════════ STEAL HALF ═════════════════════════════════

    [Fact]
    public void TryStealHalf_SingleItemInLifo_ReturnsItDirectly()
    {
        var victim = new BoundedLocalQueue<string>(MakeOverflow<string>());
        victim.Push("a");

        var thief = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.True(victim.TryStealHalf(thief, out var item));
        Assert.Equal("a", item);
        Assert.True(victim.IsEmpty);
        Assert.True(thief.IsEmpty);
    }

    [Fact]
    public void TryStealHalf_TwoItemsInRing_StealsOne()
    {
        var victim = new BoundedLocalQueue<string>(MakeOverflow<string>());
        victim.Push("a");
        victim.Push("b");
        Assert.True(victim.TryPop(out var lifo));
        Assert.Equal("b", lifo);

        var thief = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.True(victim.TryStealHalf(thief, out var item));
        Assert.Equal("a", item);
        Assert.True(victim.IsEmpty);
        Assert.True(thief.IsEmpty);
    }

    [Fact]
    public void TryStealHalf_FourItemsInRing_StealsCeilHalf()
    {
        var victim = new BoundedLocalQueue<string>(MakeOverflow<string>());
        for (var i = 0; i < 5; i++)
            victim.Push(i.ToString());
        Assert.True(victim.TryPop(out _));

        var thief = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.True(victim.TryStealHalf(thief, out var firstItem));

        var all = new HashSet<string> { firstItem };
        while (thief.TryPop(out var t))
            Assert.True(all.Add(t));

        while (victim.TryPop(out var v))
            Assert.True(all.Add(v));

        Assert.Equal(4, all.Count);
        for (var i = 0; i < 4; i++)
            Assert.Contains(i.ToString(), all);
    }

    [Fact]
    public void TryStealHalf_EightItemsInRing_StealsHalf()
    {
        var victim = new BoundedLocalQueue<string>(MakeOverflow<string>());
        for (var i = 0; i < 9; i++)
            victim.Push(i.ToString());
        Assert.True(victim.TryPop(out _));

        var thief = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.True(victim.TryStealHalf(thief, out var firstItem));

        var all = new HashSet<string> { firstItem };
        while (thief.TryPop(out var t))
            Assert.True(all.Add(t));
        while (victim.TryPop(out var v))
            Assert.True(all.Add(v));

        Assert.Equal(8, all.Count);
    }

    [Fact]
    public void TryStealHalf_LargeCount_NoItemsLostOrDuplicated()
    {
        const int Count = 128;
        var victim = new BoundedLocalQueue<string>(MakeOverflow<string>());
        for (var i = 0; i <= Count; i++)
            victim.Push(i.ToString());
        Assert.True(victim.TryPop(out _));

        var thief = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.True(victim.TryStealHalf(thief, out var firstItem));

        var all = new HashSet<string> { firstItem };
        while (thief.TryPop(out var t))
            Assert.True(all.Add(t), $"Duplicate in thief: {t}");
        while (victim.TryPop(out var v))
            Assert.True(all.Add(v), $"Duplicate in victim: {v}");

        Assert.Equal(Count, all.Count);
        for (var i = 0; i < Count; i++)
            Assert.Contains(i.ToString(), all);
    }

    [Fact]
    public void TryStealHalf_CascadeDistribution_FullyDistributes()
    {
        const int Count = 16;
        var overflow = MakeOverflow<string>();
        var source = new BoundedLocalQueue<string>(overflow);
        for (var i = 0; i <= Count; i++)
            source.Push(i.ToString());
        Assert.True(source.TryPop(out _));

        var all = new HashSet<string>();

        var d1 = new BoundedLocalQueue<string>(overflow);
        Assert.True(source.TryStealHalf(d1, out var item1));
        all.Add(item1);

        var d2 = new BoundedLocalQueue<string>(overflow);
        Assert.True(source.TryStealHalf(d2, out var item2));
        all.Add(item2);

        var d3 = new BoundedLocalQueue<string>(overflow);
        Assert.True(d1.TryStealHalf(d3, out var item3));
        all.Add(item3);

        foreach (var q in new[] { source, d1, d2, d3 })
        {
            while (q.TryPop(out var v))
                all.Add(v);
        }

        Assert.Equal(Count, all.Count);
    }

    [Fact]
    public void TryStealHalf_DestinationAlreadyHasItems_AppendsCorrectly()
    {
        var overflow = MakeOverflow<string>();
        var victim = new BoundedLocalQueue<string>(overflow);
        victim.Push("a");
        victim.Push("b");
        victim.Push("c");
        victim.Push("d");
        Assert.True(victim.TryPop(out _));

        var thief = new BoundedLocalQueue<string>(overflow);
        thief.Push("x");

        Assert.True(victim.TryStealHalf(thief, out var firstItem));

        var all = new HashSet<string> { firstItem };
        while (thief.TryPop(out var t))
            Assert.True(all.Add(t));
        while (victim.TryPop(out var v))
            Assert.True(all.Add(v));

        Assert.Contains("a", all);
        Assert.Contains("b", all);
        Assert.Contains("c", all);
        Assert.Contains("x", all);
    }

    // ═══════════════════════════ PUSH/POP CYCLES ════════════════════════════

    [Fact]
    public void RepeatedPushPop_WorksCorrectly()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());

        for (var cycle = 0; cycle < 100; cycle++)
        {
            queue.Push(cycle.ToString());
            Assert.True(queue.TryPop(out var item));
            Assert.Equal(cycle.ToString(), item);
            Assert.True(queue.IsEmpty);
        }
    }

    [Fact]
    public void RepeatedPushStealHalf_WorksCorrectly()
    {
        var overflow = MakeOverflow<string>();
        var queue = new BoundedLocalQueue<string>(overflow);

        for (var cycle = 0; cycle < 100; cycle++)
        {
            queue.Push(cycle.ToString());
            var thief = new BoundedLocalQueue<string>(overflow);
            Assert.True(queue.TryStealHalf(thief, out var item));
            Assert.Equal(cycle.ToString(), item);
            Assert.True(queue.IsEmpty);
        }
    }

    // ═══════════════════════════ COUNT ═══════════════════════════════════════

    [Fact]
    public void Count_TracksCorrectly_ThroughPushAndPop()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());

        for (var i = 0; i < 10; i++)
        {
            queue.Push(i.ToString());
            Assert.Equal(i + 1, queue.Count);
        }

        for (var i = 9; i >= 0; i--)
        {
            Assert.True(queue.TryPop(out _));
            Assert.Equal(i, queue.Count);
        }
    }

    // ═══════════════════════════ CAPACITY ════════════════════════════════════

    [Fact]
    public void Capacity_Is256()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.Equal(256, queue.GetTestAccessor().Capacity);
    }

    [Fact]
    public void Push_FillsRingToCapacity_NoOverflow()
    {
        var overflow = MakeOverflow<string>();
        var queue = new BoundedLocalQueue<string>(overflow);

        for (var i = 0; i < BoundedLocalQueue<string>.QueueCapacity; i++)
            queue.Push(i.ToString());

        var all = new HashSet<string>();
        while (queue.TryPop(out var item))
            Assert.True(all.Add(item));

        Assert.Equal(BoundedLocalQueue<string>.QueueCapacity, all.Count);
        Assert.True(overflow.IsEmpty);
    }

    // ═══════════════════════════ LIFO SLOT DETAILS ══════════════════════════

    [Fact]
    public void LifoSlot_StealableWhenRingEmpty()
    {
        var overflow = MakeOverflow<string>();
        var queue = new BoundedLocalQueue<string>(overflow);
        queue.Push("only-in-lifo");

        Assert.True(queue.GetTestAccessor().HasLifoItem);
        Assert.Equal(0, queue.GetTestAccessor().RingCount);

        var thief = new BoundedLocalQueue<string>(overflow);
        Assert.True(queue.TryStealHalf(thief, out var stolen));
        Assert.Equal("only-in-lifo", stolen);
        Assert.False(queue.GetTestAccessor().HasLifoItem);
    }

    [Fact]
    public void LifoSlot_StealHalfFallsBackToLifo_WhenRingEmpty()
    {
        var overflow = MakeOverflow<string>();
        var victim = new BoundedLocalQueue<string>(overflow);
        victim.Push("lifo-only");

        var thief = new BoundedLocalQueue<string>(overflow);
        Assert.True(victim.TryStealHalf(thief, out var item));
        Assert.Equal("lifo-only", item);
        Assert.True(thief.IsEmpty);
        Assert.True(victim.IsEmpty);
    }

    // ═══════════════════════════ POP EMPTY AFTER USAGE ══════════════════════

    [Fact]
    public void TryPop_AfterAllStolen_ReturnsFalse()
    {
        var overflow = MakeOverflow<string>();
        var queue = new BoundedLocalQueue<string>(overflow);
        queue.Push("a");
        queue.Push("b");

        var thief = new BoundedLocalQueue<string>(overflow);
        Assert.True(queue.TryStealHalf(thief, out _));
        while (queue.TryPop(out _)) { }
        while (thief.TryPop(out _)) { }

        Assert.False(queue.TryPop(out _));
    }

    [Fact]
    public void TryStealHalf_AfterAllPopped_ReturnsFalse()
    {
        var overflow = MakeOverflow<string>();
        var queue = new BoundedLocalQueue<string>(overflow);
        queue.Push("a");
        queue.Push("b");
        Assert.True(queue.TryPop(out _));
        Assert.True(queue.TryPop(out _));

        var thief = new BoundedLocalQueue<string>(overflow);
        Assert.False(queue.TryStealHalf(thief, out _));
    }

    // ═══════════════════════════ OVERFLOW ═════════════════════════════════════

    [Fact]
    public void Push_WhenFull_OverflowsHalfPlusNewItem()
    {
        var overflow = MakeOverflow<string>();
        var queue = new BoundedLocalQueue<string>(overflow);
        var cap = BoundedLocalQueue<string>.QueueCapacity;

        for (var i = 0; i <= cap; i++)
            queue.Push($"item-{i}");

        Assert.True(overflow.IsEmpty);

        queue.Push("trigger");

        Assert.Equal((cap / 2) + 1, overflow.Count);

        var overflowed = overflow.ToList();
        for (var i = 0; i < cap / 2; i++)
            Assert.Equal($"item-{i}", overflowed[i]);

        Assert.Equal($"item-{cap}", overflowed[^1]);
    }

    [Fact]
    public void Push_WhenFull_RingRetainsNewerHalf()
    {
        var overflow = MakeOverflow<string>();
        var queue = new BoundedLocalQueue<string>(overflow);
        var cap = BoundedLocalQueue<string>.QueueCapacity;

        for (var i = 0; i <= cap; i++)
            queue.Push($"item-{i}");

        queue.Push("trigger");

        var popped = new List<string>();
        while (queue.TryPop(out var item))
            popped.Add(item);

        Assert.Equal("trigger", popped[0]);
        for (var i = 1; i < popped.Count; i++)
            Assert.Equal($"item-{(cap / 2) - 1 + i}", popped[i]);

        Assert.Equal((cap / 2) + 1, popped.Count);
    }

    [Fact]
    public void Push_WhenFull_NoItemsLostOrDuplicated()
    {
        var overflow = MakeOverflow<string>();
        var queue = new BoundedLocalQueue<string>(overflow);
        const int TotalItems = 1000;

        for (var i = 0; i < TotalItems; i++)
            queue.Push($"item-{i}");

        var popped = new List<string>();
        while (queue.TryPop(out var item))
            popped.Add(item);

        var all = new HashSet<string>(overflow);
        foreach (var p in popped)
            Assert.True(all.Add(p), $"Duplicate item: {p}");

        Assert.Equal(TotalItems, all.Count);
    }

    [Fact]
    public void Push_RepeatedOverflow_AllItemsAccountedFor()
    {
        var overflow = MakeOverflow<string>();
        var queue = new BoundedLocalQueue<string>(overflow);
        const int TotalItems = 5000;

        var popped = new List<string>();
        for (var i = 0; i < TotalItems; i++)
        {
            queue.Push($"item-{i}");
            if (i % 3 == 0 && queue.TryPop(out var item))
                popped.Add(item);
        }

        while (queue.TryPop(out var remaining))
            popped.Add(remaining);

        var all = new HashSet<string>(overflow);
        foreach (var p in popped)
            Assert.True(all.Add(p), $"Duplicate item: {p}");

        Assert.Equal(TotalItems, all.Count);
    }
}
