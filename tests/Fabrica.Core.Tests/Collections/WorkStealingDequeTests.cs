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
}
