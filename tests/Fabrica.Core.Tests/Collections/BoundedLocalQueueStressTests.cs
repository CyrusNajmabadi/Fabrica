using System.Runtime.CompilerServices;
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
        var overflow = new StrongBox<InjectionQueue<Box>>(new InjectionQueue<Box>());
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
                    var firstItem = queue.TryStealHalf(ref thiefDeque);
                    if (firstItem != null)
                    {
                        stolen.Add(firstItem.Value);
                        for (var local = thiefDeque.TryPop(); local != null; local = thiefDeque.TryPop())
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
        var overflow = new StrongBox<InjectionQueue<Box>>(new InjectionQueue<Box>());
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
                    var firstItem = queue.TryStealHalf(ref thiefDeque);
                    if (firstItem != null)
                    {
                        stolen.Add(firstItem.Value);
                        for (var local = thiefDeque.TryPop(); local != null; local = thiefDeque.TryPop())
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

            if (i % popEveryN == 0)
            {
                var item = queue.TryPop();
                if (item != null) ownerPopped.Add(item.Value);
            }
        }

        for (var remaining = queue.TryPop(); remaining != null; remaining = queue.TryPop())
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
        var overflow = new StrongBox<InjectionQueue<Box>>(new InjectionQueue<Box>());
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
                        var firstItem = queue.TryStealHalf(ref thiefDeque);
                        if (firstItem != null)
                        {
                            bag.Add(firstItem.Value);
                            for (var local = thiefDeque.TryPop(); local != null; local = thiefDeque.TryPop())
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

            if (i % popEveryN == 0)
            {
                var item = queue.TryPop();
                if (item != null) ownerPopped.Add(item.Value);
            }
        }

        for (var remaining = queue.TryPop(); remaining != null; remaining = queue.TryPop())
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
        var overflow = new StrongBox<InjectionQueue<Box>>(new InjectionQueue<Box>());
        var queue = new BoundedLocalQueue<Box>(overflow);

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
                        var firstItem = queue.TryStealHalf(ref thiefDeque);
                        if (firstItem != null)
                        {
                            bag.Add(firstItem.Value);
                            for (var local = thiefDeque.TryPop(); local != null; local = thiefDeque.TryPop())
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
        var overflow = new StrongBox<InjectionQueue<Box>>(new InjectionQueue<Box>());
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
                        var firstItem = queue.TryStealHalf(ref thiefDeque);
                        if (firstItem != null)
                        {
                            bag.Add(firstItem.Value);
                            for (var local = thiefDeque.TryPop(); local != null; local = thiefDeque.TryPop())
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

            if (i % popEveryN == 0)
            {
                var item = queue.TryPop();
                if (item != null) ownerPopped.Add(item.Value);
            }
        }

        for (var remaining = queue.TryPop(); remaining != null; remaining = queue.TryPop())
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
        var overflow = new StrongBox<InjectionQueue<Box>>(new InjectionQueue<Box>());
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
                        var firstItem = queue.TryStealHalf(ref thiefDeque);
                        if (firstItem != null)
                        {
                            bag.Add(firstItem.Value);
                            for (var local = thiefDeque.TryPop(); local != null; local = thiefDeque.TryPop())
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

            var item = queue.TryPop();
            if (item != null)
                ownerPopped.Add(item.Value);
        }

        for (var remaining = queue.TryPop(); remaining != null; remaining = queue.TryPop())
            ownerPopped.Add(remaining.Value);

        ownerDone.Set();
        thiefBarrier.Wait(TestContext.Current.CancellationToken);

        AssertNoThiefExceptions(ref thiefException);
        AssertAllItemsAccountedFor(itemCount, ownerPopped, stolenBags, overflow);
    }

    // ═══════════════════════════ OVERFLOW → INJECTION UNDER STEAL PRESSURE ═
    // Tokio stress1/stress2 pattern: owner pushes far beyond capacity, forcing
    // repeated overflow to injection queue, while thieves steal concurrently.

    [Theory]
    [InlineData(1_000, 1)]
    [InlineData(5_000, 2)]
    [InlineData(10_000, 3)]
    [InlineData(20_000, 4)]
    public void Stress_OverflowToInjection_UnderStealPressure_NoItemsLost(
        int itemCount, int thiefCount)
    {
        var overflow = new StrongBox<InjectionQueue<Box>>(new InjectionQueue<Box>());
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
                        var firstItem = queue.TryStealHalf(ref thiefDeque);
                        if (firstItem != null)
                        {
                            bag.Add(firstItem.Value);
                            for (var local = thiefDeque.TryPop(); local != null; local = thiefDeque.TryPop())
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

            if (i % 7 == 0)
            {
                var item = queue.TryPop();
                if (item != null) ownerPopped.Add(item.Value);
            }
        }

        for (var remaining = queue.TryPop(); remaining != null; remaining = queue.TryPop())
            ownerPopped.Add(remaining.Value);

        ownerDone.Set();
        thiefBarrier.Wait(TestContext.Current.CancellationToken);

        AssertNoThiefExceptions(ref thiefException);

        var allStolen = new List<int>();
        foreach (var bag in stolenBags)
            allStolen.AddRange(bag);

        AssertAllItemsAccountedFor(itemCount, ownerPopped, allStolen, overflow);
    }

    // ═══════════════════════════ CHAINED STEAL ════════════════════════════
    // Tokio chained_steal pattern: thread A steals from B while B steals from C.

    [Theory]
    [InlineData(10_000)]
    [InlineData(50_000)]
    [InlineData(200_000)]
    public void Stress_ChainedSteal_NoItemsLost(int itemCount)
    {
        var overflow = new StrongBox<InjectionQueue<Box>>(new InjectionQueue<Box>());
        var queueA = new BoundedLocalQueue<Box>(overflow);
        var queueB = new BoundedLocalQueue<Box>(overflow);
        var queueC = new BoundedLocalQueue<Box>(overflow);

        var collectedA = new List<int>();
        var collectedB = new List<int>();
        var collectedC = new List<int>();

        var ownerDone = new ManualResetEventSlim(false);
        var barrier = new CountdownEvent(2);
        Exception? threadException = null;

        // Thread B: steals from C, gets stolen from by A
        var threadB = new Thread(() =>
        {
            try
            {
                while (!ownerDone.IsSet || !queueC.IsEmpty || !queueB.IsEmpty)
                {
                    var item = queueC.TryStealHalf(ref queueB);
                    if (item != null)
                        collectedB.Add(item.Value);

                    item = queueB.TryPop();
                    if (item != null)
                        collectedB.Add(item.Value);
                    else
                        Thread.SpinWait(10);
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref threadException, ex);
            }

            barrier.Signal();
        });

        // Thread A: steals from B
        var threadA = new Thread(() =>
        {
            try
            {
                while (!ownerDone.IsSet || !queueB.IsEmpty || !queueC.IsEmpty)
                {
                    var item = queueB.TryStealHalf(ref queueA);
                    if (item != null)
                    {
                        collectedA.Add(item.Value);
                        for (var local = queueA.TryPop(); local != null; local = queueA.TryPop())
                            collectedA.Add(local.Value);
                    }
                    else
                    {
                        Thread.SpinWait(10);
                    }
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref threadException, ex);
            }

            barrier.Signal();
        });

        threadB.Start();
        threadA.Start();

        // Owner of queueC: push items
        for (var i = 0; i < itemCount; i++)
        {
            queueC.Push(new Box(i));

            if (i % 5 == 0)
            {
                var item = queueC.TryPop();
                if (item != null) collectedC.Add(item.Value);
            }
        }

        for (var remaining = queueC.TryPop(); remaining != null; remaining = queueC.TryPop())
            collectedC.Add(remaining.Value);

        ownerDone.Set();
        barrier.Wait(TestContext.Current.CancellationToken);

        AssertNoThiefExceptions(ref threadException);

        var all = new HashSet<int>(collectedC);
        foreach (var v in collectedB) Assert.True(all.Add(v), $"Duplicate: {v}");
        foreach (var v in collectedA) Assert.True(all.Add(v), $"Duplicate: {v}");
        foreach (var item in overflow.Value.GetTestAccessor().DrainToList())
            Assert.True(all.Add(item.Value), $"Duplicate in overflow: {item.Value}");

        Assert.Equal(itemCount, all.Count);
    }

    // ═══════════════════════════ HOT SLOT CONTENTION ═══════════════════════
    // Tokio basic_lifo / multi_stealer_lifo: owner push/pop of hot slot while
    // thieves try to steal it via Interlocked.Exchange.

    [Theory]
    [InlineData(50_000, 1)]
    [InlineData(50_000, 2)]
    [InlineData(100_000, 4)]
    public void Stress_HotSlotContention_NoItemsLost(int itemCount, int thiefCount)
    {
        var overflow = new StrongBox<InjectionQueue<Box>>(new InjectionQueue<Box>());
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
                        var firstItem = queue.TryStealHalf(ref thiefDeque);
                        if (firstItem != null)
                        {
                            bag.Add(firstItem.Value);
                            for (var local = thiefDeque.TryPop(); local != null; local = thiefDeque.TryPop())
                                bag.Add(local.Value);
                        }
                        else
                        {
                            Thread.SpinWait(1);
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

        // Push then immediately pop — items live only in the hot slot briefly,
        // forcing maximum contention on _hotSlot between owner and thieves.
        for (var i = 0; i < itemCount; i++)
        {
            queue.Push(new Box(i));
            var item = queue.TryPop();
            if (item != null) ownerPopped.Add(item.Value);
        }

        ownerDone.Set();
        thiefBarrier.Wait(TestContext.Current.CancellationToken);

        AssertNoThiefExceptions(ref thiefException);
        AssertAllItemsAccountedFor(itemCount, ownerPopped, stolenBags, overflow);
    }

    // ═══════════════════════════ SYNCHRONIZED START ═══════════════════════
    // Barrier-synchronized start to maximize contention at the moment of first push.

    [Theory]
    [InlineData(10_000, 4)]
    [InlineData(50_000, 8)]
    public void Stress_SynchronizedStart_MultiThief_NoItemsLost(int itemCount, int thiefCount)
    {
        var overflow = new StrongBox<InjectionQueue<Box>>(new InjectionQueue<Box>());
        var queue = new BoundedLocalQueue<Box>(overflow);

        var ownerPopped = new List<int>();
        var stolenBags = new List<int>[thiefCount];
        var startBarrier = new Barrier(thiefCount + 1);
        var thiefBarrier = new CountdownEvent(thiefCount);
        var ownerDone = new ManualResetEventSlim(false);
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
                    startBarrier.SignalAndWait();
                    while (!ownerDone.IsSet || !queue.IsEmpty)
                    {
                        var firstItem = queue.TryStealHalf(ref thiefDeque);
                        if (firstItem != null)
                        {
                            bag.Add(firstItem.Value);
                            for (var local = thiefDeque.TryPop(); local != null; local = thiefDeque.TryPop())
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

        startBarrier.SignalAndWait(TestContext.Current.CancellationToken);

        for (var i = 0; i < itemCount; i++)
        {
            queue.Push(new Box(i));
            if (i % 3 == 0)
            {
                var item = queue.TryPop();
                if (item != null) ownerPopped.Add(item.Value);
            }
        }

        for (var remaining = queue.TryPop(); remaining != null; remaining = queue.TryPop())
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
        int itemCount, List<int> ownerPopped, List<int> allStolen, StrongBox<InjectionQueue<Box>> overflow)
    {
        var all = new HashSet<int>(ownerPopped);
        foreach (var item in allStolen)
            Assert.True(all.Add(item), $"Duplicate item detected: {item}");
        foreach (var item in overflow.Value.GetTestAccessor().DrainToList())
            Assert.True(all.Add(item.Value), $"Duplicate item in overflow: {item.Value}");

        Assert.Equal(itemCount, all.Count);
    }

    private static void AssertAllItemsAccountedFor(
        int itemCount, List<int> ownerPopped, List<int>[] stolenBags, StrongBox<InjectionQueue<Box>> overflow)
    {
        var allStolen = new List<int>();
        foreach (var bag in stolenBags)
            allStolen.AddRange(bag);

        AssertAllItemsAccountedFor(itemCount, ownerPopped, allStolen, overflow);
    }
}
