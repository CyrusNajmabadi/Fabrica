using System.Collections.Concurrent;
using Fabrica.Core.Threading.Queues;
using Xunit;

namespace Fabrica.Core.Tests.Collections;

[Trait("Category", "Stress")]
public class BoundedLocalQueueStressTests
{
    // ═══════════════════════════ OWNER PUSH + SINGLE THIEF STEAL-HALF ═══════

    [Theory]
    [InlineData(10_000)]
    [InlineData(50_000)]
    [InlineData(200_000)]
    public void Stress_OwnerPushes_SingleThiefStealsHalf_NoItemsLost(int itemCount)
    {
        var overflow = new ConcurrentQueue<Box>();
        var queue = new BoundedLocalQueue<Box>(overflow);

        var stolen = new List<int>();
        var ownerDone = new ManualResetEventSlim(false);
        var thiefDone = new ManualResetEventSlim(false);
        Exception? thiefException = null;

        var thief = new Thread(() =>
        {
            try
            {
                var thiefDeque = new BoundedLocalQueue<Box>(overflow);
                while (!ownerDone.IsSet || !queue.IsEmpty)
                {
                    if (queue.TryStealHalf(thiefDeque, out var firstItem))
                    {
                        stolen.Add(firstItem.Value);
                        while (thiefDeque.TryPop(out var local))
                            stolen.Add(local.Value);
                    }
                    else
                    {
                        Thread.SpinWait(10);
                    }
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref thiefException, ex);
            }

            thiefDone.Set();
        });

        thief.Start();

        for (var i = 0; i < itemCount; i++)
            queue.Push(new Box(i));

        ownerDone.Set();
        thiefDone.Wait(TestContext.Current.CancellationToken);

        AssertNoThiefExceptions(ref thiefException);
        AssertAllItemsAccountedFor(itemCount, [], stolen, overflow);
    }

    // ═══════════════════════════ OWNER PUSH/POP + SINGLE THIEF STEAL-HALF ═══

    [Theory]
    [InlineData(10_000, 2)]
    [InlineData(50_000, 3)]
    [InlineData(50_000, 5)]
    [InlineData(200_000, 3)]
    public void Stress_OwnerPushPop_SingleThiefStealsHalf_NoItemsLost(int itemCount, int popEveryN)
    {
        var overflow = new ConcurrentQueue<Box>();
        var queue = new BoundedLocalQueue<Box>(overflow);

        var ownerPopped = new List<int>();
        var stolen = new List<int>();
        var ownerDone = new ManualResetEventSlim(false);
        var thiefDone = new ManualResetEventSlim(false);
        Exception? thiefException = null;

        var thief = new Thread(() =>
        {
            try
            {
                var thiefDeque = new BoundedLocalQueue<Box>(overflow);
                while (!ownerDone.IsSet || !queue.IsEmpty)
                {
                    if (queue.TryStealHalf(thiefDeque, out var firstItem))
                    {
                        stolen.Add(firstItem.Value);
                        while (thiefDeque.TryPop(out var local))
                            stolen.Add(local.Value);
                    }
                    else
                    {
                        Thread.SpinWait(10);
                    }
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref thiefException, ex);
            }

            thiefDone.Set();
        });

        thief.Start();

        for (var i = 0; i < itemCount; i++)
        {
            queue.Push(new Box(i));

            if (i % popEveryN == 0 && queue.TryPop(out var item))
                ownerPopped.Add(item.Value);
        }

        while (queue.TryPop(out var remaining))
            ownerPopped.Add(remaining.Value);

        ownerDone.Set();
        thiefDone.Wait(TestContext.Current.CancellationToken);

        AssertNoThiefExceptions(ref thiefException);
        AssertAllItemsAccountedFor(itemCount, ownerPopped, stolen, overflow);
    }

    // ═══════════════════════════ OWNER PUSH/POP + MULTIPLE THIEVES ═════════

    [Theory]
    [InlineData(10_000, 2, 3)]
    [InlineData(50_000, 2, 3)]
    [InlineData(50_000, 4, 5)]
    [InlineData(200_000, 4, 5)]
    public void Stress_OwnerPushPop_MultipleThievesStealsHalf_NoItemsLost(
        int itemCount, int thiefCount, int popEveryN)
    {
        var overflow = new ConcurrentQueue<Box>();
        var queue = new BoundedLocalQueue<Box>(overflow);

        var ownerPopped = new List<int>();
        var stolenBags = new List<int>[thiefCount];
        var ownerDone = new ManualResetEventSlim(false);
        var thiefBarrier = new CountdownEvent(thiefCount);
        Exception? thiefException = null;

        for (var thiefIndex = 0; thiefIndex < thiefCount; thiefIndex++)
        {
            stolenBags[thiefIndex] = [];
            var bag = stolenBags[thiefIndex];

            var thread = new Thread(() =>
            {
                try
                {
                    var thiefDeque = new BoundedLocalQueue<Box>(overflow);
                    while (!ownerDone.IsSet || !queue.IsEmpty)
                    {
                        if (queue.TryStealHalf(thiefDeque, out var firstItem))
                        {
                            bag.Add(firstItem.Value);
                            while (thiefDeque.TryPop(out var local))
                                bag.Add(local.Value);
                        }
                        else
                        {
                            Thread.SpinWait(10);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref thiefException, ex);
                }

                thiefBarrier.Signal();
            });

            thread.Start();
        }

        for (var i = 0; i < itemCount; i++)
        {
            queue.Push(new Box(i));

            if (i % popEveryN == 0 && queue.TryPop(out var item))
                ownerPopped.Add(item.Value);
        }

        while (queue.TryPop(out var remaining))
            ownerPopped.Add(remaining.Value);

        ownerDone.Set();
        thiefBarrier.Wait(TestContext.Current.CancellationToken);

        AssertNoThiefExceptions(ref thiefException);
        AssertAllItemsAccountedFor(itemCount, ownerPopped, stolenBags, overflow);
    }

    // ═══════════════════════════ STEAL HALF UNDER CONTENTION ═══════════════

    [Theory]
    [InlineData(10_000, 2)]
    [InlineData(50_000, 2)]
    [InlineData(50_000, 3)]
    [InlineData(200_000, 4)]
    public void Stress_OwnerPushes_MultipleThievesStealHalf_NoItemsLost(
        int itemCount, int thiefCount)
    {
        var overflow = new ConcurrentQueue<Box>();
        var queue = new BoundedLocalQueue<Box>(overflow);

        var stolenBags = new List<int>[thiefCount];
        var ownerDone = new ManualResetEventSlim(false);
        var thiefBarrier = new CountdownEvent(thiefCount);
        Exception? thiefException = null;

        for (var thiefIndex = 0; thiefIndex < thiefCount; thiefIndex++)
        {
            stolenBags[thiefIndex] = [];
            var bag = stolenBags[thiefIndex];
            var thiefDeque = new BoundedLocalQueue<Box>(overflow);

            var thread = new Thread(() =>
            {
                try
                {
                    while (!ownerDone.IsSet || !queue.IsEmpty)
                    {
                        if (queue.TryStealHalf(thiefDeque, out var firstItem))
                        {
                            bag.Add(firstItem.Value);
                            while (thiefDeque.TryPop(out var local))
                                bag.Add(local.Value);
                        }
                        else
                        {
                            Thread.SpinWait(10);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref thiefException, ex);
                }

                thiefBarrier.Signal();
            });

            thread.Start();
        }

        for (var i = 0; i < itemCount; i++)
            queue.Push(new Box(i));

        ownerDone.Set();
        thiefBarrier.Wait(TestContext.Current.CancellationToken);

        AssertNoThiefExceptions(ref thiefException);
        AssertAllItemsAccountedFor(itemCount, [], stolenBags, overflow);
    }

    // ═══════════════════════════ THE BUG SCENARIO ══════════════════════════
    // This is the exact scenario that broke Chase-Lev: concurrent owner push/pop
    // with multiple thieves doing steal-half. The TOCTOU between reading _bottom
    // and CASing _top allowed duplicates. The packed-head design prevents this.

    [Theory]
    [InlineData(10_000, 2, 3)]
    [InlineData(50_000, 3, 5)]
    [InlineData(50_000, 4, 7)]
    [InlineData(200_000, 4, 7)]
    public void Stress_OwnerPushPop_MultipleThievesStealHalf_NoItemsLost(
        int itemCount, int thiefCount, int popEveryN)
    {
        var overflow = new ConcurrentQueue<Box>();
        var queue = new BoundedLocalQueue<Box>(overflow);

        var ownerPopped = new List<int>();
        var stolenBags = new List<int>[thiefCount];
        var ownerDone = new ManualResetEventSlim(false);
        var thiefBarrier = new CountdownEvent(thiefCount);
        Exception? thiefException = null;

        for (var thiefIndex = 0; thiefIndex < thiefCount; thiefIndex++)
        {
            stolenBags[thiefIndex] = [];
            var bag = stolenBags[thiefIndex];
            var thiefDeque = new BoundedLocalQueue<Box>(overflow);

            var thread = new Thread(() =>
            {
                try
                {
                    while (!ownerDone.IsSet || !queue.IsEmpty)
                    {
                        if (queue.TryStealHalf(thiefDeque, out var firstItem))
                        {
                            bag.Add(firstItem.Value);
                            while (thiefDeque.TryPop(out var local))
                                bag.Add(local.Value);
                        }
                        else
                        {
                            Thread.SpinWait(10);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref thiefException, ex);
                }

                thiefBarrier.Signal();
            });

            thread.Start();
        }

        for (var i = 0; i < itemCount; i++)
        {
            queue.Push(new Box(i));

            if (i % popEveryN == 0 && queue.TryPop(out var item))
                ownerPopped.Add(item.Value);
        }

        while (queue.TryPop(out var remaining))
            ownerPopped.Add(remaining.Value);

        ownerDone.Set();
        thiefBarrier.Wait(TestContext.Current.CancellationToken);

        AssertNoThiefExceptions(ref thiefException);
        AssertAllItemsAccountedFor(itemCount, ownerPopped, stolenBags, overflow);
    }

    // ═══════════════════════════ RAPID PUSH/POP + STEAL HALF ═══════════════

    [Theory]
    [InlineData(10_000, 1)]
    [InlineData(50_000, 2)]
    [InlineData(50_000, 3)]
    [InlineData(200_000, 3)]
    public void Stress_RapidPushPopCycles_ThievesStealHalf_NoItemsLost(
        int itemCount, int thiefCount)
    {
        var overflow = new ConcurrentQueue<Box>();
        var queue = new BoundedLocalQueue<Box>(overflow);

        var ownerPopped = new List<int>();
        var stolenBags = new List<int>[thiefCount];
        var ownerDone = new ManualResetEventSlim(false);
        var thiefBarrier = new CountdownEvent(thiefCount);
        Exception? thiefException = null;

        for (var thiefIndex = 0; thiefIndex < thiefCount; thiefIndex++)
        {
            stolenBags[thiefIndex] = [];
            var bag = stolenBags[thiefIndex];
            var thiefDeque = new BoundedLocalQueue<Box>(overflow);

            var thread = new Thread(() =>
            {
                try
                {
                    while (!ownerDone.IsSet || !queue.IsEmpty)
                    {
                        if (queue.TryStealHalf(thiefDeque, out var firstItem))
                        {
                            bag.Add(firstItem.Value);
                            while (thiefDeque.TryPop(out var local))
                                bag.Add(local.Value);
                        }
                        else
                        {
                            Thread.SpinWait(10);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref thiefException, ex);
                }

                thiefBarrier.Signal();
            });

            thread.Start();
        }

        for (var i = 0; i < itemCount; i++)
        {
            queue.Push(new Box(i));

            if (queue.TryPop(out var item))
                ownerPopped.Add(item.Value);
        }

        while (queue.TryPop(out var remaining))
            ownerPopped.Add(remaining.Value);

        ownerDone.Set();
        thiefBarrier.Wait(TestContext.Current.CancellationToken);

        AssertNoThiefExceptions(ref thiefException);
        AssertAllItemsAccountedFor(itemCount, ownerPopped, stolenBags, overflow);
    }

    // ═══════════════════════════ HELPERS ═══════════════════════════════════

    internal sealed class Box(int value)
    {
        public int Value { get; } = value;
    }

    private static void AssertNoThiefExceptions(ref Exception? thiefException)
    {
        var ex = Volatile.Read(ref thiefException);
        if (ex != null)
            Assert.Fail($"Thief thread threw an exception: {ex}");
    }

    private static void AssertAllItemsAccountedFor(
        int itemCount, List<int> ownerPopped, List<int> allStolen, ConcurrentQueue<Box> overflow)
    {
        var all = new HashSet<int>(ownerPopped);
        foreach (var item in allStolen)
            Assert.True(all.Add(item), $"Duplicate item detected: {item}");
        foreach (var item in overflow)
            Assert.True(all.Add(item.Value), $"Duplicate item in overflow: {item.Value}");

        Assert.Equal(itemCount, all.Count);
    }

    private static void AssertAllItemsAccountedFor(
        int itemCount, List<int> ownerPopped, List<int>[] stolenBags, ConcurrentQueue<Box> overflow)
    {
        var allStolen = new List<int>();
        foreach (var bag in stolenBags)
            allStolen.AddRange(bag);

        AssertAllItemsAccountedFor(itemCount, ownerPopped, allStolen, overflow);
    }
}
