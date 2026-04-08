using Fabrica.Core.Threading.Queues;
using Xunit;

namespace Fabrica.Core.Tests.Collections;

public class BoundedLocalQueueTests
{
    // ═══════════════════════════ EMPTY QUEUE ═════════════════════════════════

    [Fact]
    public void NewQueue_IsEmpty()
    {
        var queue = new BoundedLocalQueue<string>();
        Assert.True(queue.IsEmpty);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void TryPop_OnEmpty_ReturnsFalse()
    {
        var queue = new BoundedLocalQueue<string>();
        Assert.False(queue.TryPop(out var item));
        Assert.Null(item);
    }

    [Fact]
    public void TrySteal_OnEmpty_ReturnsFalse()
    {
        var queue = new BoundedLocalQueue<string>();
        Assert.False(queue.TrySteal(out var item));
        Assert.Null(item);
    }

    [Fact]
    public void TryStealHalf_OnEmpty_ReturnsFalse()
    {
        var victim = new BoundedLocalQueue<string>();
        var thief = new BoundedLocalQueue<string>();
        Assert.False(victim.TryStealHalf(thief, out var item));
        Assert.Null(item);
    }

    // ═══════════════════════════ PUSH / POP (LIFO SLOT) ═════════════════════

    [Fact]
    public void Push_SingleItem_PopReturnsIt()
    {
        var queue = new BoundedLocalQueue<string>();
        queue.Push("a");

        Assert.Equal(1, queue.Count);
        Assert.True(queue.TryPop(out var item));
        Assert.Equal("a", item);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void Push_TwoItems_PopReturnsNewestFirst_ThenOldest()
    {
        // First push goes to LIFO slot. Second push evicts first to ring, takes LIFO slot.
        // Pop returns LIFO slot (newest), then ring head (oldest).
        var queue = new BoundedLocalQueue<string>();
        queue.Push("a");
        queue.Push("b");

        Assert.True(queue.TryPop(out var first));
        Assert.Equal("b", first); // LIFO slot (newest)

        Assert.True(queue.TryPop(out var second));
        Assert.Equal("a", second); // ring buffer (evicted from LIFO)

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void Push_ThreeItems_PopReturnsLifoThenFifo()
    {
        var queue = new BoundedLocalQueue<string>();
        queue.Push("a"); // → LIFO slot
        queue.Push("b"); // → LIFO slot; "a" evicted to ring[0]
        queue.Push("c"); // → LIFO slot; "b" evicted to ring[1]

        Assert.True(queue.TryPop(out var first));
        Assert.Equal("c", first); // LIFO slot

        // Ring has [a, b] — pop from head (FIFO within the ring)
        Assert.True(queue.TryPop(out var second));
        Assert.Equal("a", second);

        Assert.True(queue.TryPop(out var third));
        Assert.Equal("b", third);

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void Pop_AfterAllPopped_ReturnsFalse()
    {
        var queue = new BoundedLocalQueue<string>();
        queue.Push("a");
        Assert.True(queue.TryPop(out _));
        Assert.False(queue.TryPop(out _));
    }

    // ═══════════════════════════ PUSH / STEAL ═══════════════════════════════

    [Fact]
    public void Push_SingleItem_StealReturnsIt()
    {
        var queue = new BoundedLocalQueue<string>();
        queue.Push("a");

        // Single item is in LIFO slot. Steal takes it.
        Assert.True(queue.TrySteal(out var item));
        Assert.Equal("a", item);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void Push_TwoItems_StealTakesRingFirst_ThenLifo()
    {
        var queue = new BoundedLocalQueue<string>();
        queue.Push("a"); // → LIFO slot
        queue.Push("b"); // → LIFO slot; "a" evicted to ring

        // Steal takes from ring first (head = "a"), then LIFO ("b")
        Assert.True(queue.TrySteal(out var first));
        Assert.Equal("a", first);

        Assert.True(queue.TrySteal(out var second));
        Assert.Equal("b", second);

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void Steal_AfterAllStolen_ReturnsFalse()
    {
        var queue = new BoundedLocalQueue<string>();
        queue.Push("a");
        Assert.True(queue.TrySteal(out _));
        Assert.False(queue.TrySteal(out _));
    }

    // ═══════════════════════════ INTERLEAVED POP + STEAL ════════════════════

    [Fact]
    public void Push_Three_StealTakesOldest_PopTakesNewest()
    {
        var queue = new BoundedLocalQueue<string>();
        queue.Push("a");
        queue.Push("b");
        queue.Push("c");

        // Steal: takes ring head (oldest in ring = "a")
        Assert.True(queue.TrySteal(out var stolen));
        Assert.Equal("a", stolen);

        // Pop: takes LIFO slot first ("c")
        Assert.True(queue.TryPop(out var popped));
        Assert.Equal("c", popped);

        // Remaining: "b" in ring
        Assert.Equal(1, queue.Count);
        Assert.True(queue.TryPop(out var last));
        Assert.Equal("b", last);
    }

    // ═══════════════════════════ STEAL HALF ═════════════════════════════════

    [Fact]
    public void TryStealHalf_SingleItemInLifo_ReturnsItDirectly()
    {
        var victim = new BoundedLocalQueue<string>();
        victim.Push("a"); // in LIFO slot only

        var thief = new BoundedLocalQueue<string>();
        Assert.True(victim.TryStealHalf(thief, out var item));
        Assert.Equal("a", item);
        Assert.True(victim.IsEmpty);
        Assert.True(thief.IsEmpty);
    }

    [Fact]
    public void TryStealHalf_TwoItemsInRing_StealsOne()
    {
        var victim = new BoundedLocalQueue<string>();
        victim.Push("a"); // → LIFO
        victim.Push("b"); // → LIFO; "a" evicted to ring
        // Pop LIFO to leave only ring items
        Assert.True(victim.TryPop(out var lifo));
        Assert.Equal("b", lifo);

        // Ring has 1 item ("a"). ceil(1/2) = 1. Returned directly.
        var thief = new BoundedLocalQueue<string>();
        Assert.True(victim.TryStealHalf(thief, out var item));
        Assert.Equal("a", item);
        Assert.True(victim.IsEmpty);
        Assert.True(thief.IsEmpty);
    }

    [Fact]
    public void TryStealHalf_FourItemsInRing_StealsCeilHalf()
    {
        var victim = new BoundedLocalQueue<string>();
        // Push 5, pop LIFO to leave 4 in ring
        for (var i = 0; i < 5; i++)
            victim.Push(i.ToString());
        Assert.True(victim.TryPop(out _)); // removes LIFO ("4")

        // Ring: [0, 1, 2, 3]. ceil(4/2) = 2 stolen.
        var thief = new BoundedLocalQueue<string>();
        Assert.True(victim.TryStealHalf(thief, out var firstItem));

        // One returned directly, one in thief's ring
        var all = new HashSet<string> { firstItem };
        while (thief.TryPop(out var t))
            Assert.True(all.Add(t));

        // Victim retains the newer half
        while (victim.TryPop(out var v))
            Assert.True(all.Add(v));

        Assert.Equal(4, all.Count);
        for (var i = 0; i < 4; i++)
            Assert.Contains(i.ToString(), all);
    }

    [Fact]
    public void TryStealHalf_EightItemsInRing_StealsHalf()
    {
        var victim = new BoundedLocalQueue<string>();
        // Push 9, pop LIFO to leave 8 in ring
        for (var i = 0; i < 9; i++)
            victim.Push(i.ToString());
        Assert.True(victim.TryPop(out _));

        var thief = new BoundedLocalQueue<string>();
        Assert.True(victim.TryStealHalf(thief, out var firstItem));

        var all = new HashSet<string> { firstItem };
        while (thief.TryPop(out var t))
            Assert.True(all.Add(t));
        while (victim.TrySteal(out var v))
            Assert.True(all.Add(v));

        Assert.Equal(8, all.Count);
    }

    [Fact]
    public void TryStealHalf_LargeCount_NoItemsLostOrDuplicated()
    {
        const int Count = 128;
        var victim = new BoundedLocalQueue<string>();
        // Push Count+1, pop LIFO to leave Count in ring
        for (var i = 0; i <= Count; i++)
            victim.Push(i.ToString());
        Assert.True(victim.TryPop(out _));

        var thief = new BoundedLocalQueue<string>();
        Assert.True(victim.TryStealHalf(thief, out var firstItem));

        var all = new HashSet<string> { firstItem };
        while (thief.TryPop(out var t))
            Assert.True(all.Add(t), $"Duplicate in thief: {t}");
        while (victim.TrySteal(out var v))
            Assert.True(all.Add(v), $"Duplicate in victim: {v}");

        Assert.Equal(Count, all.Count);
        for (var i = 0; i < Count; i++)
            Assert.Contains(i.ToString(), all);
    }

    [Fact]
    public void TryStealHalf_CascadeDistribution_FullyDistributes()
    {
        const int Count = 16;
        var source = new BoundedLocalQueue<string>();
        for (var i = 0; i <= Count; i++)
            source.Push(i.ToString());
        Assert.True(source.TryPop(out _)); // remove LIFO

        var all = new HashSet<string>();

        var d1 = new BoundedLocalQueue<string>();
        Assert.True(source.TryStealHalf(d1, out var item1));
        all.Add(item1);

        var d2 = new BoundedLocalQueue<string>();
        Assert.True(source.TryStealHalf(d2, out var item2));
        all.Add(item2);

        var d3 = new BoundedLocalQueue<string>();
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
        var victim = new BoundedLocalQueue<string>();
        victim.Push("a");
        victim.Push("b");
        victim.Push("c");
        victim.Push("d");
        Assert.True(victim.TryPop(out _)); // remove LIFO ("d"), ring = [a, b, c]

        var thief = new BoundedLocalQueue<string>();
        thief.Push("x"); // thief has "x" in LIFO

        // Steal half of victim's ring [a, b, c] → ceil(3/2) = 2
        Assert.True(victim.TryStealHalf(thief, out var firstItem));

        var all = new HashSet<string> { firstItem };
        while (thief.TryPop(out var t))
            Assert.True(all.Add(t));
        while (victim.TryPop(out var v))
            Assert.True(all.Add(v));

        // All original items accounted for
        Assert.Contains("a", all);
        Assert.Contains("b", all);
        Assert.Contains("c", all);
        Assert.Contains("x", all);
    }

    // ═══════════════════════════ PUSH/POP CYCLES ════════════════════════════

    [Fact]
    public void RepeatedPushPop_WorksCorrectly()
    {
        var queue = new BoundedLocalQueue<string>();

        for (var cycle = 0; cycle < 100; cycle++)
        {
            queue.Push(cycle.ToString());
            Assert.True(queue.TryPop(out var item));
            Assert.Equal(cycle.ToString(), item);
            Assert.True(queue.IsEmpty);
        }
    }

    [Fact]
    public void RepeatedPushSteal_WorksCorrectly()
    {
        var queue = new BoundedLocalQueue<string>();

        for (var cycle = 0; cycle < 100; cycle++)
        {
            queue.Push(cycle.ToString());
            Assert.True(queue.TrySteal(out var item));
            Assert.Equal(cycle.ToString(), item);
            Assert.True(queue.IsEmpty);
        }
    }

    // ═══════════════════════════ COUNT ═══════════════════════════════════════

    [Fact]
    public void Count_TracksCorrectly_ThroughPushAndPop()
    {
        var queue = new BoundedLocalQueue<string>();

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
        var queue = new BoundedLocalQueue<string>();
        Assert.Equal(256, queue.GetTestAccessor().Capacity);
    }

    [Fact]
    public void Push_FillsRingToCapacity_NoOverflow()
    {
        var queue = new BoundedLocalQueue<string>();

        // Push Capacity items: 1 in LIFO slot, Capacity-1 in ring.
        // Ring capacity is 256, so Capacity total items should be fine.
        for (var i = 0; i < BoundedLocalQueue<string>.QueueCapacity; i++)
            queue.Push(i.ToString());

        // Verify all items are recoverable
        var all = new HashSet<string>();
        while (queue.TryPop(out var item))
            Assert.True(all.Add(item));

        Assert.Equal(BoundedLocalQueue<string>.QueueCapacity, all.Count);
    }

    // ═══════════════════════════ LIFO SLOT DETAILS ══════════════════════════

    [Fact]
    public void LifoSlot_StealableWhenRingEmpty()
    {
        var queue = new BoundedLocalQueue<string>();
        queue.Push("only-in-lifo");

        Assert.True(queue.GetTestAccessor().HasLifoItem);
        Assert.Equal(0, queue.GetTestAccessor().RingCount);

        Assert.True(queue.TrySteal(out var stolen));
        Assert.Equal("only-in-lifo", stolen);
        Assert.False(queue.GetTestAccessor().HasLifoItem);
    }

    [Fact]
    public void LifoSlot_StealHalfFallsBackToLifo_WhenRingEmpty()
    {
        var victim = new BoundedLocalQueue<string>();
        victim.Push("lifo-only");

        var thief = new BoundedLocalQueue<string>();
        Assert.True(victim.TryStealHalf(thief, out var item));
        Assert.Equal("lifo-only", item);
        Assert.True(thief.IsEmpty);
        Assert.True(victim.IsEmpty);
    }

    // ═══════════════════════════ POP EMPTY AFTER USAGE ══════════════════════

    [Fact]
    public void TryPop_AfterAllStolen_ReturnsFalse()
    {
        var queue = new BoundedLocalQueue<string>();
        queue.Push("a");
        queue.Push("b");
        Assert.True(queue.TrySteal(out _));
        Assert.True(queue.TrySteal(out _));
        Assert.False(queue.TryPop(out _));
    }

    [Fact]
    public void TrySteal_AfterAllPopped_ReturnsFalse()
    {
        var queue = new BoundedLocalQueue<string>();
        queue.Push("a");
        queue.Push("b");
        Assert.True(queue.TryPop(out _));
        Assert.True(queue.TryPop(out _));
        Assert.False(queue.TrySteal(out _));
    }

    // ═══════════════════════════ OVERFLOW ═════════════════════════════════════

    [Fact]
    public void Push_WhenFull_OverflowsHalfPlusNewItem()
    {
        var overflowed = new List<string>();
        var queue = new BoundedLocalQueue<string>(item => overflowed.Add(item));
        var cap = BoundedLocalQueue<string>.QueueCapacity;

        // Fill ring: 1st push → LIFO only. Pushes 2..257 evict into ring.
        // After cap+1 pushes: LIFO has item-{cap}, ring has items 0..{cap-1} (256 items, full).
        for (var i = 0; i <= cap; i++)
            queue.Push($"item-{i}");

        Assert.Empty(overflowed);

        // The (cap+2)th push evicts LIFO → PushToRingBuffer → overflow.
        queue.Push("trigger");

        // Overflow: 128 oldest ring items (item-0..item-127) + the evicted item (item-{cap}).
        Assert.Equal(cap / 2 + 1, overflowed.Count);
        for (var i = 0; i < cap / 2; i++)
            Assert.Equal($"item-{i}", overflowed[i]);

        Assert.Equal($"item-{cap}", overflowed[^1]);
    }

    [Fact]
    public void Push_WhenFull_RingRetainsNewerHalf()
    {
        var overflowed = new List<string>();
        var queue = new BoundedLocalQueue<string>(item => overflowed.Add(item));
        var cap = BoundedLocalQueue<string>.QueueCapacity;

        for (var i = 0; i <= cap; i++)
            queue.Push($"item-{i}");

        queue.Push("trigger");

        var popped = new List<string>();
        while (queue.TryPop(out var item))
            popped.Add(item);

        // "trigger" pops first (LIFO), then ring items {cap/2}..{cap-1} (FIFO order).
        Assert.Equal("trigger", popped[0]);
        for (var i = 1; i < popped.Count; i++)
            Assert.Equal($"item-{cap / 2 - 1 + i}", popped[i]);

        // Total: 1 (trigger) + cap/2 (remaining ring half) = cap/2 + 1.
        Assert.Equal(cap / 2 + 1, popped.Count);
    }

    [Fact]
    public void Push_WhenFull_NoItemsLostOrDuplicated()
    {
        var overflowed = new List<string>();
        var queue = new BoundedLocalQueue<string>(item => overflowed.Add(item));
        const int TotalItems = 1000;

        for (var i = 0; i < TotalItems; i++)
            queue.Push($"item-{i}");

        var popped = new List<string>();
        while (queue.TryPop(out var item))
            popped.Add(item);

        var all = new HashSet<string>(overflowed);
        foreach (var p in popped)
            Assert.True(all.Add(p), $"Duplicate item: {p}");

        Assert.Equal(TotalItems, all.Count);
    }

    [Fact]
    public void Push_WhenFull_WithoutCallback_SilentlyDropsInRelease()
    {
        var queue = new BoundedLocalQueue<string>();
        var cap = BoundedLocalQueue<string>.QueueCapacity;

        // Fill ring to capacity (cap+1 pushes: 1 LIFO + cap ring).
        for (var i = 0; i <= cap; i++)
            queue.Push($"item-{i}");

        // One more push triggers overflow — no callback, so in Release the item is dropped.
        queue.Push("overflow-item");

        Assert.True(queue.Count > 0);
    }

    [Fact]
    public void Push_RepeatedOverflow_AllItemsAccountedFor()
    {
        var overflowed = new List<string>();
        var queue = new BoundedLocalQueue<string>(item => overflowed.Add(item));
        const int TotalItems = 5000;

        // Push items while periodically popping to create repeated overflow cycles.
        var popped = new List<string>();
        for (var i = 0; i < TotalItems; i++)
        {
            queue.Push($"item-{i}");
            if (i % 3 == 0 && queue.TryPop(out var item))
                popped.Add(item);
        }

        while (queue.TryPop(out var remaining))
            popped.Add(remaining);

        var all = new HashSet<string>(overflowed);
        foreach (var p in popped)
            Assert.True(all.Add(p), $"Duplicate item: {p}");

        Assert.Equal(TotalItems, all.Count);
    }
}
