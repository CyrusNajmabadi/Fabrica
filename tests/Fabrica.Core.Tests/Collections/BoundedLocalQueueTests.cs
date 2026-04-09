using System.Runtime.CompilerServices;
using Fabrica.Core.Threading.Queues;
using Xunit;

namespace Fabrica.Core.Tests.Collections;

public class BoundedLocalQueueTests
{
    private static InjectionQueue<T> MakeOverflow<T>() where T : class => new();

    // ═══════════════════════════ LAYOUT VERIFICATION ═════════════════════════

    [Fact]
    public void Layout_HeadAndTail_AreSeparatedByAtLeast128Bytes()
    {
        var ht = new CacheLinePaddedHead();
        var offset = Unsafe.ByteOffset(
            ref Unsafe.As<long, byte>(ref ht.Head),
            ref Unsafe.As<int, byte>(ref ht.Tail));
        Assert.True((long)offset >= 128,
            $"Expected Head and Tail to be >= 128 bytes apart, but they are {(long)offset} bytes apart.");
    }

    // ═══════════════════════════ EMPTY QUEUE ═════════════════════════════════

    [Fact]
    public void NewQueue_IsEmpty()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.True(queue.IsEmpty);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void TryPop_OnEmpty_ReturnsNull()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.Null(queue.TryPop());
    }

    [Fact]
    public void TryStealHalf_OnEmpty_ReturnsNull()
    {
        var victim = new BoundedLocalQueue<string>(MakeOverflow<string>());
        var thief = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.Null(victim.TryStealHalf(ref thief));
    }

    // ═══════════════════════════ PUSH / POP (LIFO SLOT) ═════════════════════

    [Fact]
    public void Push_SingleItem_PopReturnsIt()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        queue.Push("a");

        Assert.Equal(1, queue.Count);
        var item = queue.TryPop();
        Assert.Equal("a", item);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void Push_TwoItems_PopReturnsNewestFirst_ThenOldest()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        queue.Push("a");
        queue.Push("b");

        Assert.Equal("b", queue.TryPop());

        Assert.Equal("a", queue.TryPop());

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void Push_ThreeItems_PopReturnsLifoThenFifo()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        queue.Push("a");
        queue.Push("b");
        queue.Push("c");

        Assert.Equal("c", queue.TryPop());

        Assert.Equal("a", queue.TryPop());

        Assert.Equal("b", queue.TryPop());

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void Pop_AfterAllPopped_ReturnsFalse()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        queue.Push("a");
        Assert.NotNull(queue.TryPop());
        Assert.Null(queue.TryPop());
    }

    // ═══════════════════════════ PUSH / STEAL HALF ═══════════════════════════

    [Fact]
    public void Push_SingleItem_StealHalfReturnsIt()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        queue.Push("a");

        var thief = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.Equal("a", queue.TryStealHalf(ref thief));
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void Push_TwoItems_StealHalfTakesOldestFromRing()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        queue.Push("a");
        queue.Push("b");

        var thief = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.Equal("a", queue.TryStealHalf(ref thief));

        Assert.Equal("b", queue.TryPop());

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void StealHalf_AfterAllStolen_ReturnsFalse()
    {
        var queue = new BoundedLocalQueue<string>(MakeOverflow<string>());
        queue.Push("a");
        var thief = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.NotNull(queue.TryStealHalf(ref thief));
        Assert.Null(queue.TryStealHalf(ref thief));
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
        var stolen = queue.TryStealHalf(ref thief);
        Assert.NotNull(stolen);

        var popped = queue.TryPop();
        Assert.Equal("c", popped);

        var all = new HashSet<string> { stolen, popped! };
        for (var t = thief.TryPop(); t != null; t = thief.TryPop())
            Assert.True(all.Add(t));
        for (var q = queue.TryPop(); q != null; q = queue.TryPop())
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
        Assert.Equal("a", victim.TryStealHalf(ref thief));
        Assert.True(victim.IsEmpty);
        Assert.True(thief.IsEmpty);
    }

    [Fact]
    public void TryStealHalf_TwoItemsInRing_StealsOne()
    {
        var victim = new BoundedLocalQueue<string>(MakeOverflow<string>());
        victim.Push("a");
        victim.Push("b");
        Assert.Equal("b", victim.TryPop());

        var thief = new BoundedLocalQueue<string>(MakeOverflow<string>());
        Assert.Equal("a", victim.TryStealHalf(ref thief));
        Assert.True(victim.IsEmpty);
        Assert.True(thief.IsEmpty);
    }

    [Fact]
    public void TryStealHalf_FourItemsInRing_StealsCeilHalf()
    {
        var victim = new BoundedLocalQueue<string>(MakeOverflow<string>());
        for (var i = 0; i < 5; i++)
            victim.Push(i.ToString());
        Assert.NotNull(victim.TryPop());

        var thief = new BoundedLocalQueue<string>(MakeOverflow<string>());
        var firstItem = victim.TryStealHalf(ref thief);
        Assert.NotNull(firstItem);

        var all = new HashSet<string> { firstItem };
        for (var t = thief.TryPop(); t != null; t = thief.TryPop())
            Assert.True(all.Add(t));

        for (var v = victim.TryPop(); v != null; v = victim.TryPop())
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
        Assert.NotNull(victim.TryPop());

        var thief = new BoundedLocalQueue<string>(MakeOverflow<string>());
        var firstItem = victim.TryStealHalf(ref thief);
        Assert.NotNull(firstItem);

        var all = new HashSet<string> { firstItem };
        for (var t = thief.TryPop(); t != null; t = thief.TryPop())
            Assert.True(all.Add(t));
        for (var v = victim.TryPop(); v != null; v = victim.TryPop())
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
        Assert.NotNull(victim.TryPop());

        var thief = new BoundedLocalQueue<string>(MakeOverflow<string>());
        var firstItem = victim.TryStealHalf(ref thief);
        Assert.NotNull(firstItem);

        var all = new HashSet<string> { firstItem };
        for (var t = thief.TryPop(); t != null; t = thief.TryPop())
            Assert.True(all.Add(t), $"Duplicate in thief: {t}");
        for (var v = victim.TryPop(); v != null; v = victim.TryPop())
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
        Assert.NotNull(source.TryPop());

        var all = new HashSet<string>();

        var d1 = new BoundedLocalQueue<string>(overflow);
        all.Add(source.TryStealHalf(ref d1)!);

        var d2 = new BoundedLocalQueue<string>(overflow);
        all.Add(source.TryStealHalf(ref d2)!);

        var d3 = new BoundedLocalQueue<string>(overflow);
        all.Add(d1.TryStealHalf(ref d3)!);

        for (var v = source.TryPop(); v != null; v = source.TryPop())
            all.Add(v);
        for (var v = d1.TryPop(); v != null; v = d1.TryPop())
            all.Add(v);
        for (var v = d2.TryPop(); v != null; v = d2.TryPop())
            all.Add(v);
        for (var v = d3.TryPop(); v != null; v = d3.TryPop())
            all.Add(v);

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
        Assert.NotNull(victim.TryPop());

        var thief = new BoundedLocalQueue<string>(overflow);
        thief.Push("x");

        var firstItem = victim.TryStealHalf(ref thief);
        Assert.NotNull(firstItem);

        var all = new HashSet<string> { firstItem };
        for (var t = thief.TryPop(); t != null; t = thief.TryPop())
            Assert.True(all.Add(t));
        for (var v = victim.TryPop(); v != null; v = victim.TryPop())
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
            Assert.Equal(cycle.ToString(), queue.TryPop());
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
            Assert.Equal(cycle.ToString(), queue.TryStealHalf(ref thief));
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
            Assert.NotNull(queue.TryPop());
            Assert.Equal(i, queue.Count);
        }
    }

    // ═══════════════════════════ CAPACITY ════════════════════════════════════

    [Fact]
    public void Capacity_Is256() => Assert.Equal(256, BoundedLocalQueue<string>.QueueCapacity);

    [Fact]
    public void Push_FillsRingToCapacity_NoOverflow()
    {
        var overflow = MakeOverflow<string>();
        var queue = new BoundedLocalQueue<string>(overflow);

        for (var i = 0; i < BoundedLocalQueue<string>.QueueCapacity; i++)
            queue.Push(i.ToString());

        var all = new HashSet<string>();
        for (var item = queue.TryPop(); item != null; item = queue.TryPop())
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

        Assert.True(queue.HasLifoItem);
        Assert.Equal(0, queue.RingCount);

        var thief = new BoundedLocalQueue<string>(overflow);
        Assert.Equal("only-in-lifo", queue.TryStealHalf(ref thief));
        Assert.False(queue.HasLifoItem);
    }

    [Fact]
    public void LifoSlot_StealHalfFallsBackToLifo_WhenRingEmpty()
    {
        var overflow = MakeOverflow<string>();
        var victim = new BoundedLocalQueue<string>(overflow);
        victim.Push("lifo-only");

        var thief = new BoundedLocalQueue<string>(overflow);
        Assert.Equal("lifo-only", victim.TryStealHalf(ref thief));
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
        Assert.NotNull(queue.TryStealHalf(ref thief));
        while (queue.TryPop() != null) { }
        while (thief.TryPop() != null) { }

        Assert.Null(queue.TryPop());
    }

    [Fact]
    public void TryStealHalf_AfterAllPopped_ReturnsFalse()
    {
        var overflow = MakeOverflow<string>();
        var queue = new BoundedLocalQueue<string>(overflow);
        queue.Push("a");
        queue.Push("b");
        Assert.NotNull(queue.TryPop());
        Assert.NotNull(queue.TryPop());

        var thief = new BoundedLocalQueue<string>(overflow);
        Assert.Null(queue.TryStealHalf(ref thief));
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

        // DrainToList returns LIFO order (most recently pushed first).
        // The overflow path pushes: item-0, item-1, ..., item-127, item-{cap}.
        // So drain yields: item-{cap}, item-127, ..., item-0.
        var overflowed = overflow.GetTestAccessor().DrainToList();
        Assert.Equal($"item-{cap}", overflowed[0]);
        for (var i = 1; i < overflowed.Count; i++)
            Assert.Equal($"item-{(cap / 2) - i}", overflowed[i]);
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
        for (var item = queue.TryPop(); item != null; item = queue.TryPop())
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
        for (var item = queue.TryPop(); item != null; item = queue.TryPop())
            popped.Add(item);

        var all = new HashSet<string>(overflow.GetTestAccessor().DrainToList());
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
            if (i % 3 == 0)
            {
                var item = queue.TryPop();
                if (item != null) popped.Add(item);
            }
        }

        for (var remaining = queue.TryPop(); remaining != null; remaining = queue.TryPop())
            popped.Add(remaining);

        var all = new HashSet<string>(overflow.GetTestAccessor().DrainToList());
        foreach (var p in popped)
            Assert.True(all.Add(p), $"Duplicate item: {p}");

        Assert.Equal(TotalItems, all.Count);
    }
}
