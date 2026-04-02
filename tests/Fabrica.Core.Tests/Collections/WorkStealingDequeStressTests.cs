using Fabrica.Core.Collections;
using Xunit;

namespace Fabrica.Core.Tests.Collections;

[Trait("Category", "Stress")]
public class WorkStealingDequeStressTests
{
    // ═══════════════════════════ OWNER PUSH + SINGLE THIEF STEAL ════════════

    [Theory]
    [InlineData(10_000, 4)]
    [InlineData(10_000, 64)]
    [InlineData(200_000, 4)]
    [InlineData(200_000, 64)]
    [InlineData(200_000, 1024)]
    public void Stress_OwnerPushes_SingleThiefSteals_NoItemsLost(int itemCount, int initialCapacity)
    {
        var deque = new WorkStealingDeque<int>(initialCapacity);

        var stolen = new List<int>();
        var thiefDone = new ManualResetEventSlim(false);
        var ownerDone = new ManualResetEventSlim(false);

        var thief = new Thread(() =>
        {
            while (!ownerDone.IsSet || !deque.IsEmpty)
            {
                if (deque.TrySteal(out var item))
                    stolen.Add(item);
                else
                    Thread.SpinWait(10);
            }

            thiefDone.Set();
        });

        thief.Start();

        for (var i = 0; i < itemCount; i++)
            deque.Push(i);

        ownerDone.Set();
        thiefDone.Wait(TestContext.Current.CancellationToken);

        Assert.Equal(itemCount, stolen.Count);

        for (var i = 0; i < itemCount; i++)
            Assert.Equal(i, stolen[i]);
    }

    // ═══════════════════════════ OWNER PUSH/POP + SINGLE THIEF STEAL ═══════

    [Theory]
    [InlineData(50_000, 4, 2)]
    [InlineData(50_000, 64, 3)]
    [InlineData(200_000, 4, 2)]
    [InlineData(200_000, 64, 3)]
    [InlineData(200_000, 64, 5)]
    [InlineData(200_000, 1024, 7)]
    public void Stress_OwnerPushPop_SingleThiefSteals_NoItemsLost(int itemCount, int initialCapacity, int popEveryN)
    {
        var deque = new WorkStealingDeque<int>(initialCapacity);

        var ownerPopped = new List<int>();
        var stolen = new List<int>();
        var thiefDone = new ManualResetEventSlim(false);
        var ownerDone = new ManualResetEventSlim(false);

        var thief = new Thread(() =>
        {
            while (!ownerDone.IsSet || !deque.IsEmpty)
            {
                if (deque.TrySteal(out var item))
                    stolen.Add(item);
                else
                    Thread.SpinWait(10);
            }

            thiefDone.Set();
        });

        thief.Start();

        for (var i = 0; i < itemCount; i++)
        {
            deque.Push(i);

            if (i % popEveryN == 0 && deque.TryPop(out var item))
                ownerPopped.Add(item);
        }

        while (deque.TryPop(out var remaining))
            ownerPopped.Add(remaining);

        ownerDone.Set();
        thiefDone.Wait(TestContext.Current.CancellationToken);

        AssertAllItemsAccountedFor(itemCount, ownerPopped, stolen);
    }

    // ═══════════════════════════ OWNER PUSH/POP + MULTIPLE THIEVES ═════════

    [Theory]
    [InlineData(50_000, 4, 2, 3)]
    [InlineData(50_000, 64, 4, 5)]
    [InlineData(200_000, 4, 2, 3)]
    [InlineData(200_000, 64, 4, 5)]
    [InlineData(200_000, 64, 6, 7)]
    [InlineData(200_000, 1024, 4, 3)]
    public void Stress_OwnerPushPop_MultipleThieves_NoItemsLost(
        int itemCount, int initialCapacity, int thiefCount, int popEveryN)
    {
        var deque = new WorkStealingDeque<int>(initialCapacity);

        var ownerPopped = new List<int>();
        var stolenBags = new List<int>[thiefCount];
        var ownerDone = new ManualResetEventSlim(false);
        var thiefBarrier = new CountdownEvent(thiefCount);

        for (var thiefIndex = 0; thiefIndex < thiefCount; thiefIndex++)
        {
            stolenBags[thiefIndex] = [];
            var bag = stolenBags[thiefIndex];

            var thread = new Thread(() =>
            {
                while (!ownerDone.IsSet || !deque.IsEmpty)
                {
                    if (deque.TrySteal(out var item))
                        bag.Add(item);
                    else
                        Thread.SpinWait(10);
                }

                thiefBarrier.Signal();
            });

            thread.Start();
        }

        for (var i = 0; i < itemCount; i++)
        {
            deque.Push(i);

            if (i % popEveryN == 0 && deque.TryPop(out var item))
                ownerPopped.Add(item);
        }

        while (deque.TryPop(out var remaining))
            ownerPopped.Add(remaining);

        ownerDone.Set();
        thiefBarrier.Wait(TestContext.Current.CancellationToken);

        var allStolen = new List<int>();
        foreach (var bag in stolenBags)
            allStolen.AddRange(bag);

        AssertAllItemsAccountedFor(itemCount, ownerPopped, allStolen);
    }

    // ═══════════════════════════ GROWTH UNDER CONTENTION ═══════════════════

    [Theory]
    [InlineData(50_000, 4, 2, 7)]
    [InlineData(50_000, 4, 4, 3)]
    [InlineData(100_000, 4, 2, 5)]
    [InlineData(100_000, 4, 4, 11)]
    public void Stress_GrowthUnderContention_NoItemsLost(
        int itemCount, int initialCapacity, int thiefCount, int popEveryN)
    {
        var deque = new WorkStealingDeque<int>(initialCapacity);

        var ownerPopped = new List<int>();
        var stolenBags = new List<int>[thiefCount];
        var ownerDone = new ManualResetEventSlim(false);
        var thiefBarrier = new CountdownEvent(thiefCount);

        for (var thiefIndex = 0; thiefIndex < thiefCount; thiefIndex++)
        {
            stolenBags[thiefIndex] = [];
            var bag = stolenBags[thiefIndex];

            var thread = new Thread(() =>
            {
                while (!ownerDone.IsSet || !deque.IsEmpty)
                {
                    if (deque.TrySteal(out var item))
                        bag.Add(item);
                    else
                        Thread.SpinWait(10);
                }

                thiefBarrier.Signal();
            });

            thread.Start();
        }

        for (var i = 0; i < itemCount; i++)
        {
            deque.Push(i);

            if (i % popEveryN == 0 && deque.TryPop(out var item))
                ownerPopped.Add(item);
        }

        while (deque.TryPop(out var remaining))
            ownerPopped.Add(remaining);

        ownerDone.Set();
        thiefBarrier.Wait(TestContext.Current.CancellationToken);

        var allStolen = new List<int>();
        foreach (var bag in stolenBags)
            allStolen.AddRange(bag);

        AssertAllItemsAccountedFor(itemCount, ownerPopped, allStolen);
    }

    // ═══════════════════════════ RAPID PUSH/POP CYCLES + THIEVES ═══════════

    [Theory]
    [InlineData(50_000, 4, 1)]
    [InlineData(50_000, 8, 3)]
    [InlineData(100_000, 4, 2)]
    [InlineData(100_000, 8, 4)]
    [InlineData(200_000, 64, 3)]
    public void Stress_RapidPushPopCycles_ThievesCompete_NoItemsLost(
        int itemCount, int initialCapacity, int thiefCount)
    {
        var deque = new WorkStealingDeque<int>(initialCapacity);

        var ownerPopped = new List<int>();
        var stolenBags = new List<int>[thiefCount];
        var ownerDone = new ManualResetEventSlim(false);
        var thiefBarrier = new CountdownEvent(thiefCount);

        for (var thiefIndex = 0; thiefIndex < thiefCount; thiefIndex++)
        {
            stolenBags[thiefIndex] = [];
            var bag = stolenBags[thiefIndex];

            var thread = new Thread(() =>
            {
                while (!ownerDone.IsSet || !deque.IsEmpty)
                {
                    if (deque.TrySteal(out var item))
                        bag.Add(item);
                    else
                        Thread.SpinWait(10);
                }

                thiefBarrier.Signal();
            });

            thread.Start();
        }

        for (var i = 0; i < itemCount; i++)
        {
            deque.Push(i);

            if (deque.TryPop(out var item))
                ownerPopped.Add(item);
        }

        while (deque.TryPop(out var remaining))
            ownerPopped.Add(remaining);

        ownerDone.Set();
        thiefBarrier.Wait(TestContext.Current.CancellationToken);

        var allStolen = new List<int>();
        foreach (var bag in stolenBags)
            allStolen.AddRange(bag);

        AssertAllItemsAccountedFor(itemCount, ownerPopped, allStolen);
    }

    // ═══════════════════════════ HELPERS ═══════════════════════════════════

    private static void AssertAllItemsAccountedFor(int itemCount, List<int> ownerPopped, List<int> allStolen)
    {
        var all = new HashSet<int>(ownerPopped);
        foreach (var item in allStolen)
            Assert.True(all.Add(item), $"Duplicate item detected: {item}");

        Assert.Equal(itemCount, all.Count);

        for (var i = 0; i < itemCount; i++)
            Assert.Contains(i, all);
    }

    private static void AssertAllItemsAccountedFor(int itemCount, List<int> ownerPopped, List<int>[] stolenBags)
    {
        var allStolen = new List<int>();
        foreach (var bag in stolenBags)
            allStolen.AddRange(bag);

        AssertAllItemsAccountedFor(itemCount, ownerPopped, allStolen);
    }
}
