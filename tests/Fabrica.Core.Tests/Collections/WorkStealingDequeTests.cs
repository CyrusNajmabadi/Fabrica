using Fabrica.Core.Threading.Queues;
using Xunit;

namespace Fabrica.Core.Tests.Collections;

public class WorkStealingDequeTests
{
    // ═══════════════════════════ EMPTY DEQUE ═════════════════════════════════

    [Fact]
    public void NewDeque_IsEmpty()
    {
        var deque = new WorkStealingDeque<int>();
        Assert.True(deque.IsEmpty);
        Assert.Equal(0, deque.Count);
    }

    [Fact]
    public void TryPop_OnEmptyDeque_ReturnsFalse()
    {
        var deque = new WorkStealingDeque<int>();
        Assert.False(deque.TryPop(out var item));
        Assert.Equal(default, item);
    }

    [Fact]
    public void TrySteal_OnEmptyDeque_ReturnsFalse()
    {
        var deque = new WorkStealingDeque<int>();
        Assert.False(deque.TrySteal(out var item));
        Assert.Equal(default, item);
    }

    // ═══════════════════════════ PUSH / POP (LIFO) ══════════════════════════

    [Fact]
    public void Push_SingleItem_PopReturnsIt()
    {
        var deque = new WorkStealingDeque<int>();
        deque.Push(42);

        Assert.Equal(1, deque.Count);
        Assert.True(deque.TryPop(out var item));
        Assert.Equal(42, item);
        Assert.True(deque.IsEmpty);
    }

    [Fact]
    public void Push_MultipleItems_PopReturnsInLifoOrder()
    {
        var deque = new WorkStealingDeque<int>();
        deque.Push(1);
        deque.Push(2);
        deque.Push(3);

        Assert.True(deque.TryPop(out var first));
        Assert.Equal(3, first);
        Assert.True(deque.TryPop(out var second));
        Assert.Equal(2, second);
        Assert.True(deque.TryPop(out var third));
        Assert.Equal(1, third);
        Assert.True(deque.IsEmpty);
    }

    [Fact]
    public void Pop_AfterAllItemsPopped_ReturnsFalse()
    {
        var deque = new WorkStealingDeque<int>();
        deque.Push(1);
        Assert.True(deque.TryPop(out _));
        Assert.False(deque.TryPop(out _));
    }

    // ═══════════════════════════ PUSH / STEAL (FIFO) ═════════════════════════

    [Fact]
    public void Push_SingleItem_StealReturnsIt()
    {
        var deque = new WorkStealingDeque<int>();
        deque.Push(42);

        Assert.True(deque.TrySteal(out var item));
        Assert.Equal(42, item);
        Assert.True(deque.IsEmpty);
    }

    [Fact]
    public void Push_MultipleItems_StealReturnsInFifoOrder()
    {
        var deque = new WorkStealingDeque<int>();
        deque.Push(1);
        deque.Push(2);
        deque.Push(3);

        Assert.True(deque.TrySteal(out var first));
        Assert.Equal(1, first);
        Assert.True(deque.TrySteal(out var second));
        Assert.Equal(2, second);
        Assert.True(deque.TrySteal(out var third));
        Assert.Equal(3, third);
        Assert.True(deque.IsEmpty);
    }

    [Fact]
    public void Steal_AfterAllItemsStolen_ReturnsFalse()
    {
        var deque = new WorkStealingDeque<int>();
        deque.Push(1);
        Assert.True(deque.TrySteal(out _));
        Assert.False(deque.TrySteal(out _));
    }

    // ═══════════════════════════ INTERLEAVED POP + STEAL ════════════════════

    [Fact]
    public void Push_ThreeItems_StealTakesOldest_PopTakesNewest()
    {
        var deque = new WorkStealingDeque<int>();
        deque.Push(1);
        deque.Push(2);
        deque.Push(3);

        Assert.True(deque.TrySteal(out var stolen));
        Assert.Equal(1, stolen);

        Assert.True(deque.TryPop(out var popped));
        Assert.Equal(3, popped);

        Assert.Equal(1, deque.Count);
        Assert.True(deque.TryPop(out var last));
        Assert.Equal(2, last);
    }

    [Fact]
    public void Interleaved_PushPopSteal_MaintainsCorrectOrdering()
    {
        var deque = new WorkStealingDeque<int>();

        deque.Push(10);
        deque.Push(20);
        deque.Push(30);

        Assert.True(deque.TrySteal(out var stolen1));
        Assert.Equal(10, stolen1);

        deque.Push(40);

        Assert.True(deque.TryPop(out var popped1));
        Assert.Equal(40, popped1);

        Assert.True(deque.TrySteal(out var stolen2));
        Assert.Equal(20, stolen2);

        Assert.True(deque.TryPop(out var popped2));
        Assert.Equal(30, popped2);

        Assert.True(deque.IsEmpty);
    }

    // ═══════════════════════════ LAST-ELEMENT RACES (DETERMINISTIC) ═════════

    [Fact]
    public void Pop_SingleElement_SucceedsWithNoConcurrentStealer()
    {
        var deque = new WorkStealingDeque<int>();
        deque.Push(99);
        Assert.True(deque.TryPop(out var item));
        Assert.Equal(99, item);
    }

    [Fact]
    public void Steal_SingleElement_SucceedsWithNoConcurrentOwner()
    {
        var deque = new WorkStealingDeque<int>();
        deque.Push(99);
        Assert.True(deque.TrySteal(out var item));
        Assert.Equal(99, item);
    }

#if DEBUG
    [Fact]
    public void TryPop_LastElement_CasLost_WhenThiefStealsFirst()
    {
        // Exercises the CAS-lost path in TryPop. When the owner is about to CAS on the last element, a thief that started
        // its steal earlier (before the owner decremented bottom) wins the CAS first. We simulate this with
        // SimulateStealCas, which directly advances _top (bypassing the bottom check that would fail now that the owner
        // has already decremented it).
        var deque = new WorkStealingDeque<int>();
        deque.Push(42);

        var accessor = deque.GetTestAccessor();
        var stolenItem = -1;

        accessor.DebugBeforePopCas = () =>
        {
            Assert.True(accessor.SimulateStealCas(out stolenItem));
        };

        Assert.False(deque.TryPop(out var poppedItem));
        Assert.Equal(default, poppedItem);
        Assert.Equal(42, stolenItem);
        Assert.True(deque.IsEmpty);
    }

    [Fact]
    public void TryPop_LastElement_CasLost_DequeIsEmptyAndCountIsZero()
    {
        var deque = new WorkStealingDeque<int>();
        deque.Push(7);

        var accessor = deque.GetTestAccessor();

        accessor.DebugBeforePopCas = () =>
        {
            accessor.SimulateStealCas(out _);
        };

        Assert.False(deque.TryPop(out _));
        Assert.True(deque.IsEmpty);
        Assert.Equal(0, deque.Count);
    }

    [Fact]
    public void TrySteal_CasLost_WhenAnotherThiefStealsFirst()
    {
        // Exercises the CAS-lost path in TrySteal. After the first thief reads the item but before its CAS, a second
        // thief steals the same item by advancing top first. The first thief's CAS then fails.
        var deque = new WorkStealingDeque<int>();
        deque.Push(100);
        deque.Push(200);

        var interceptedItem = -1;

        var accessor = deque.GetTestAccessor();
        accessor.DebugBeforeStealCas = () =>
        {
            accessor.DebugBeforeStealCas = null;
            Assert.True(deque.TrySteal(out interceptedItem));
        };

        Assert.False(deque.TrySteal(out var item));
        Assert.Equal(default, item);

        Assert.Equal(100, interceptedItem);
        Assert.Equal(1, deque.Count);

        Assert.True(deque.TrySteal(out var remaining));
        Assert.Equal(200, remaining);
    }

    [Fact]
    public void TrySteal_CasLost_WhenOwnerPopsLastElement()
    {
        // Exercises the CAS-lost path in TrySteal when the owner pops the last element. The thief reads top and sees one
        // item, but before its CAS, the owner pops it (advancing top via TryPop's CAS). The thief's CAS then fails.
        var deque = new WorkStealingDeque<int>();
        deque.Push(50);

        var accessor = deque.GetTestAccessor();

        accessor.DebugBeforeStealCas = () =>
        {
            accessor.SimulateStealCas(out _);
        };

        Assert.False(deque.TrySteal(out var item));
        Assert.Equal(default, item);
        Assert.True(deque.IsEmpty);
    }
#endif

    // ═══════════════════════════ GROWTH ══════════════════════════════════════

    [Fact]
    public void Push_BeyondInitialCapacity_GrowsBuffer()
    {
        var deque = WorkStealingDeque<int>.TestAccessor.Create(4);
        var accessor = deque.GetTestAccessor();
        Assert.Equal(4, accessor.Capacity);

        for (var i = 0; i < 5; i++)
            deque.Push(i);

        Assert.Equal(8, accessor.Capacity);
        Assert.Equal(5, deque.Count);

        for (var i = 4; i >= 0; i--)
        {
            Assert.True(deque.TryPop(out var item));
            Assert.Equal(i, item);
        }
    }

    [Fact]
    public void Growth_PreservesAllItems_WhenReadViaSteal()
    {
        var deque = WorkStealingDeque<int>.TestAccessor.Create(4);

        for (var i = 0; i < 20; i++)
            deque.Push(i);

        for (var i = 0; i < 20; i++)
        {
            Assert.True(deque.TrySteal(out var item));
            Assert.Equal(i, item);
        }
    }

    [Fact]
    public void MultipleGrowths_PreserveAllItems()
    {
        var deque = WorkStealingDeque<int>.TestAccessor.Create(4);

        for (var i = 0; i < 100; i++)
            deque.Push(i);

        Assert.True(deque.GetTestAccessor().Capacity >= 100);

        for (var i = 99; i >= 0; i--)
        {
            Assert.True(deque.TryPop(out var item));
            Assert.Equal(i, item);
        }
    }

    [Fact]
    public void Growth_FromPartiallyConsumedDeque_PreservesLiveItems()
    {
        var deque = WorkStealingDeque<int>.TestAccessor.Create(4);

        for (var i = 0; i < 4; i++)
            deque.Push(i);

        Assert.True(deque.TrySteal(out var first));
        Assert.Equal(0, first);
        Assert.True(deque.TrySteal(out var second));
        Assert.Equal(1, second);

        for (var i = 10; i < 14; i++)
            deque.Push(i);

        Assert.Equal(6, deque.Count);

        Assert.True(deque.TryPop(out var popped));
        Assert.Equal(13, popped);
        Assert.True(deque.TrySteal(out var stolen));
        Assert.Equal(2, stolen);
    }

    // ═══════════════════════════ PUSH/POP CYCLES (REUSE BUFFER SLOTS) ═══════

    [Fact]
    public void RepeatedPushPop_ReusesSlotsCorrectly()
    {
        var deque = WorkStealingDeque<int>.TestAccessor.Create(4);
        var accessor = deque.GetTestAccessor();

        for (var cycle = 0; cycle < 50; cycle++)
        {
            deque.Push(cycle);
            Assert.True(deque.TryPop(out var item));
            Assert.Equal(cycle, item);
            Assert.True(deque.IsEmpty);
        }

        Assert.Equal(4, accessor.Capacity);
    }

    [Fact]
    public void RepeatedPushSteal_ReusesSlotsCorrectly()
    {
        var deque = WorkStealingDeque<int>.TestAccessor.Create(4);
        var accessor = deque.GetTestAccessor();

        for (var cycle = 0; cycle < 50; cycle++)
        {
            deque.Push(cycle);
            Assert.True(deque.TrySteal(out var item));
            Assert.Equal(cycle, item);
            Assert.True(deque.IsEmpty);
        }

        Assert.Equal(4, accessor.Capacity);
    }

    // ═══════════════════════════ REFERENCE TYPES ════════════════════════════

    [Fact]
    public void WorksWithReferenceTypes()
    {
        var deque = new WorkStealingDeque<string>();
        deque.Push("hello");
        deque.Push("world");

        Assert.True(deque.TrySteal(out var first));
        Assert.Equal("hello", first);
        Assert.True(deque.TryPop(out var second));
        Assert.Equal("world", second);
    }

    // ═══════════════════════════ CAPACITY ROUNDING ══════════════════════════

    [Fact]
    public void Constructor_RoundsCapacityUpToPowerOfTwo()
    {
        var deque = WorkStealingDeque<int>.TestAccessor.Create(5);
        Assert.Equal(8, deque.GetTestAccessor().Capacity);
    }

    [Fact]
    public void Constructor_EnforcesMinimumCapacity()
    {
        var deque = WorkStealingDeque<int>.TestAccessor.Create(1);
        Assert.Equal(4, deque.GetTestAccessor().Capacity);
    }

    [Fact]
    public void Constructor_PowerOfTwoCapacity_StaysAsIs()
    {
        var deque = WorkStealingDeque<int>.TestAccessor.Create(16);
        Assert.Equal(16, deque.GetTestAccessor().Capacity);
    }

    [Fact]
    public void DefaultCapacity_Is32()
    {
        var deque = new WorkStealingDeque<int>();
        Assert.Equal(32, deque.GetTestAccessor().Capacity);
    }

    // ═══════════════════════════ COUNT ACCURACY ══════════════════════════════

    [Fact]
    public void Count_TracksCorrectly_ThroughPushAndPop()
    {
        var deque = new WorkStealingDeque<int>();

        for (var i = 0; i < 10; i++)
        {
            deque.Push(i);
            Assert.Equal(i + 1, deque.Count);
        }

        for (var i = 9; i >= 0; i--)
        {
            Assert.True(deque.TryPop(out _));
            Assert.Equal(i, deque.Count);
        }
    }

    [Fact]
    public void Count_TracksCorrectly_ThroughPushAndSteal()
    {
        var deque = new WorkStealingDeque<int>();

        for (var i = 0; i < 10; i++)
            deque.Push(i);

        for (var i = 9; i >= 0; i--)
        {
            Assert.True(deque.TrySteal(out _));
            Assert.Equal(i, deque.Count);
        }
    }

    // ═══════════════════════════ POP ON EMPTY AFTER USAGE ═══════════════════

    [Fact]
    public void TryPop_AfterPushAndStealAll_ReturnsFalse()
    {
        var deque = new WorkStealingDeque<int>();
        deque.Push(1);
        deque.Push(2);
        Assert.True(deque.TrySteal(out _));
        Assert.True(deque.TrySteal(out _));

        Assert.False(deque.TryPop(out _));
        Assert.True(deque.IsEmpty);
    }

    [Fact]
    public void TrySteal_AfterPushAndPopAll_ReturnsFalse()
    {
        var deque = new WorkStealingDeque<int>();
        deque.Push(1);
        deque.Push(2);
        Assert.True(deque.TryPop(out _));
        Assert.True(deque.TryPop(out _));

        Assert.False(deque.TrySteal(out _));
        Assert.True(deque.IsEmpty);
    }

    // ═══════════════════════════ STEAL HALF ══════════════════════════════════

    [Fact]
    public void TryStealHalf_EmptySource_ReturnsFalse()
    {
        var victim = new WorkStealingDeque<int>();
        var thief = new WorkStealingDeque<int>();

        Assert.False(victim.TryStealHalf(thief, out var item));
        Assert.Equal(default, item);
        Assert.True(thief.IsEmpty);
    }

    [Fact]
    public void TryStealHalf_SingleItem_ReturnsItDirectly_NothingInDestination()
    {
        var victim = new WorkStealingDeque<int>();
        victim.Push(42);

        var thief = new WorkStealingDeque<int>();
        Assert.True(victim.TryStealHalf(thief, out var item));
        Assert.Equal(42, item);
        Assert.True(victim.IsEmpty);
        Assert.True(thief.IsEmpty);
    }

    [Fact]
    public void TryStealHalf_TwoItems_StealsCeilHalf_ReturnsOneDirectly()
    {
        var victim = new WorkStealingDeque<int>();
        victim.Push(1);
        victim.Push(2);

        var thief = new WorkStealingDeque<int>();
        Assert.True(victim.TryStealHalf(thief, out var item));

        // ceil(2/2) = 1 item stolen total. Returned directly, nothing in destination.
        Assert.Equal(1, item);
        Assert.True(thief.IsEmpty);
        Assert.Equal(1, victim.Count);

        Assert.True(victim.TrySteal(out var remaining));
        Assert.Equal(2, remaining);
    }

    [Fact]
    public void TryStealHalf_FourItems_StealsCeilHalf()
    {
        var victim = new WorkStealingDeque<int>();
        for (var i = 1; i <= 4; i++)
            victim.Push(i);

        var thief = new WorkStealingDeque<int>();
        Assert.True(victim.TryStealHalf(thief, out var firstItem));

        Assert.Equal(1, firstItem);
        Assert.Equal(1, thief.Count);
        Assert.Equal(2, victim.Count);

        Assert.True(thief.TryPop(out var thiefItem));
        Assert.Equal(2, thiefItem);

        Assert.True(victim.TrySteal(out var remaining1));
        Assert.Equal(3, remaining1);
        Assert.True(victim.TrySteal(out var remaining2));
        Assert.Equal(4, remaining2);
    }

    [Fact]
    public void TryStealHalf_EightItems_StealsHalf_InFifoOrder()
    {
        var victim = new WorkStealingDeque<int>();
        for (var i = 1; i <= 8; i++)
            victim.Push(i);

        var thief = new WorkStealingDeque<int>();
        Assert.True(victim.TryStealHalf(thief, out var firstItem));

        Assert.Equal(1, firstItem);
        Assert.Equal(3, thief.Count);
        Assert.Equal(4, victim.Count);

        // Thief's deque: items 2,3,4 (stolen from FIFO end), pop gives LIFO order
        Assert.True(thief.TryPop(out var t1));
        Assert.Equal(4, t1);
        Assert.True(thief.TryPop(out var t2));
        Assert.Equal(3, t2);
        Assert.True(thief.TryPop(out var t3));
        Assert.Equal(2, t3);

        // Victim retains the newer half: 5,6,7,8
        for (var expected = 5; expected <= 8; expected++)
        {
            Assert.True(victim.TrySteal(out var v));
            Assert.Equal(expected, v);
        }
    }

    [Fact]
    public void TryStealHalf_LargeCount_NoItemsLostOrDuplicated()
    {
        const int Count = 64;
        var victim = new WorkStealingDeque<int>();
        for (var i = 0; i < Count; i++)
            victim.Push(i);

        var thief = new WorkStealingDeque<int>();
        Assert.True(victim.TryStealHalf(thief, out var firstItem));

        var all = new HashSet<int> { firstItem };

        while (thief.TryPop(out var item))
            Assert.True(all.Add(item));

        while (victim.TrySteal(out var item))
            Assert.True(all.Add(item));

        Assert.Equal(Count, all.Count);
        for (var i = 0; i < Count; i++)
            Assert.Contains(i, all);
    }

    [Fact]
    public void TryStealHalf_CascadeDistribution_FullyDistributes()
    {
        var source = new WorkStealingDeque<int>();
        for (var i = 0; i < 16; i++)
            source.Push(i);

        var all = new HashSet<int>();

        var d1 = new WorkStealingDeque<int>();
        Assert.True(source.TryStealHalf(d1, out var item1));
        all.Add(item1);

        var d2 = new WorkStealingDeque<int>();
        Assert.True(source.TryStealHalf(d2, out var item2));
        all.Add(item2);

        var d3 = new WorkStealingDeque<int>();
        Assert.True(d1.TryStealHalf(d3, out var item3));
        all.Add(item3);

        // Drain remaining from all deques
        foreach (var deque in new[] { source, d1, d2, d3 })
        {
            while (deque.TryPop(out var v))
                all.Add(v);
        }

        Assert.Equal(16, all.Count);
    }

    [Fact]
    public void TryStealHalf_DestinationGrowsIfNeeded()
    {
        var victim = new WorkStealingDeque<int>();
        for (var i = 0; i < 64; i++)
            victim.Push(i);

        var thief = WorkStealingDeque<int>.TestAccessor.Create(4);
        Assert.True(victim.TryStealHalf(thief, out _));

        Assert.True(thief.GetTestAccessor().Capacity >= 31);
    }

    [Fact]
    public void TryStealHalf_DestinationAlreadyHasItems_AppendsCorrectly()
    {
        var victim = new WorkStealingDeque<int>();
        victim.Push(10);
        victim.Push(20);
        victim.Push(30);
        victim.Push(40);

        var thief = new WorkStealingDeque<int>();
        thief.Push(100);

        Assert.True(victim.TryStealHalf(thief, out var firstItem));
        Assert.Equal(10, firstItem);

        Assert.Equal(2, thief.Count);

        Assert.True(thief.TryPop(out var newest));
        Assert.Equal(20, newest);
        Assert.True(thief.TryPop(out var oldest));
        Assert.Equal(100, oldest);
    }
}
