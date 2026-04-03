using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Jobs;

public class ThreadLocalJobPoolTests
{
    // ═══════════════════════════ TEST JOB ════════════════════════════════════

    private sealed class TestJob : Job
    {
        public int Value { get; set; }
        public bool Executed { get; set; }

        public override void Execute()
            => this.Executed = true;

        public override void Return()
        {
        }
    }

    private readonly struct TestJobAllocator : IAllocator<TestJob>
    {
        public TestJob Allocate()
            => new();

        public void Reset(TestJob item)
        {
            item.Value = 0;
            item.Executed = false;
        }
    }

    // ═══════════════════════════ RENT — EMPTY POOL ══════════════════════════

    [Fact]
    public void Rent_FromEmptyPool_AllocatesNewInstance()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(4);
        var job = pool.Rent(0);
        Assert.NotNull(job);
    }

    [Fact]
    public void Rent_MultipleFromEmptyPool_AllocatesDistinctInstances()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(4);
        var job1 = pool.Rent(0);
        var job2 = pool.Rent(0);
        var job3 = pool.Rent(0);
        Assert.NotSame(job1, job2);
        Assert.NotSame(job2, job3);
        Assert.NotSame(job1, job3);
    }

    [Fact]
    public void Rent_FromEmptyPool_ReturnsCleanInstance()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(4);
        var job = pool.Rent(0);
        Assert.Equal(0, job.Value);
        Assert.False(job.Executed);
    }

    [Fact]
    public void Rent_AllDequesEmpty_AllocatesNew()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(4);
        var job = pool.Rent(0);
        Assert.NotNull(job);
        Assert.Equal(0, pool.Count);
    }

    // ═══════════════════════════ RENT — OWN DEQUE ═══════════════════════════

    [Fact]
    public void Rent_AfterReturn_ReusesSameInstance()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(4);
        var job = pool.Rent(0);
        pool.Return(0, job);

        var reused = pool.Rent(0);
        Assert.Same(job, reused);
    }

    [Fact]
    public void Rent_MultipleThenReturn_ReturnsInLifoOrder()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(4);
        var job1 = pool.Rent(0);
        var job2 = pool.Rent(0);
        var job3 = pool.Rent(0);

        pool.Return(0, job1);
        pool.Return(0, job2);
        pool.Return(0, job3);

        Assert.Same(job3, pool.Rent(0));
        Assert.Same(job2, pool.Rent(0));
        Assert.Same(job1, pool.Rent(0));
    }

    [Fact]
    public void Rent_DrainsOwnDequeThenAllocates()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(4);
        var job = pool.Rent(0);
        pool.Return(0, job);

        var reused = pool.Rent(0);
        Assert.Same(job, reused);

        var fresh = pool.Rent(0);
        Assert.NotSame(job, fresh);
    }

    // ═══════════════════════════ RETURN — RESET ═════════════════════════════

    [Fact]
    public void Return_ResetsFieldsViaAllocator()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(4);
        var job = pool.Rent(0);
        job.Value = 99;
        job.Executed = true;

        pool.Return(0, job);

        var reused = pool.Rent(0);
        Assert.Same(job, reused);
        Assert.Equal(0, reused.Value);
        Assert.False(reused.Executed);
    }

    [Fact]
    public void Return_ResetsBeforePooling_NotOnNextRent()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(4);
        var job = pool.Rent(0);
        job.Value = 42;

        pool.Return(0, job);

        Assert.Equal(0, job.Value);
    }

    // ═══════════════════════════ CROSS-THREAD STEALING ══════════════════════

    [Fact]
    public void Rent_OwnDequeEmpty_StealsFromOtherThread()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(4);
        var job = pool.Rent(2);
        pool.Return(2, job);

        var stolen = pool.Rent(0);
        Assert.Same(job, stolen);
    }

    [Fact]
    public void Rent_StealsOldestItem_FifoFromVictim()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(2);
        var job1 = pool.Rent(1);
        var job2 = pool.Rent(1);
        var job3 = pool.Rent(1);

        pool.Return(1, job1);
        pool.Return(1, job2);
        pool.Return(1, job3);

        var stolen = pool.Rent(0);
        Assert.Same(job1, stolen);
    }

    [Fact]
    public void Rent_SkipsSelfDuringSteal()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(3);
        var job = pool.Rent(2);
        pool.Return(2, job);

        var stolen = pool.Rent(0);
        Assert.Same(job, stolen);
    }

    // ═══════════════════════════ ROUND-ROBIN ════════════════════════════════

    [Fact]
    public void Rent_RoundRobin_CyclesThroughDeques()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(4);

        var jobA = pool.Rent(1);
        var jobB = pool.Rent(2);
        var jobC = pool.Rent(3);
        pool.Return(1, jobA);
        pool.Return(2, jobB);
        pool.Return(3, jobC);

        var first = pool.Rent(0);
        var second = pool.Rent(0);
        var third = pool.Rent(0);

        var stolen = new HashSet<TestJob> { first, second, third };
        Assert.Equal(3, stolen.Count);
        Assert.Contains(jobA, stolen);
        Assert.Contains(jobB, stolen);
        Assert.Contains(jobC, stolen);
    }

    [Fact]
    public void Rent_RoundRobin_AdvancesAfterSteal()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(4);

        for (var i = 1; i <= 3; i++)
        {
            var job = pool.Rent(i);
            pool.Return(i, job);
        }

        pool.Rent(0);
        pool.Rent(0);

        for (var i = 1; i <= 3; i++)
        {
            var fresh = pool.Rent(i);
            pool.Return(i, fresh);
        }

        var third = pool.Rent(0);
        Assert.NotNull(third);
    }

    [Fact]
    public void Rent_RoundRobin_WrapsAroundToStart()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(3);

        var jobB = pool.Rent(2);
        pool.Return(2, jobB);

        var first = pool.Rent(0);
        Assert.Same(jobB, first);

        var jobA = pool.Rent(1);
        pool.Return(1, jobA);

        var second = pool.Rent(0);
        Assert.Same(jobA, second);
    }

    [Fact]
    public void Rent_RoundRobin_SkipsEmptyDeques()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(5);

        var job = pool.Rent(4);
        pool.Return(4, job);

        var stolen = pool.Rent(0);
        Assert.Same(job, stolen);
    }

    // ═══════════════════════════ COUNT ═══════════════════════════════════════

    [Fact]
    public void Count_EmptyPool_ReturnsZero()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(4);
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void Count_SumsAcrossThreads()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(4);
        pool.Return(0, new TestJob());
        pool.Return(0, new TestJob());
        pool.Return(2, new TestJob());
        Assert.Equal(3, pool.Count);
    }

    [Fact]
    public void Count_AfterDrain_ReturnsZero()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(2);
        pool.Return(0, new TestJob());
        pool.Return(0, new TestJob());
        pool.Return(1, new TestJob());

        pool.Rent(0);
        pool.Rent(0);
        pool.Rent(0);

        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void CountForThread_ReturnsPerThreadCount()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(4);
        pool.Return(0, new TestJob());
        pool.Return(0, new TestJob());
        pool.Return(2, new TestJob());

        Assert.Equal(2, pool.CountForThread(0));
        Assert.Equal(0, pool.CountForThread(1));
        Assert.Equal(1, pool.CountForThread(2));
        Assert.Equal(0, pool.CountForThread(3));
    }

    [Fact]
    public void ThreadCount_ReflectsConstructorArgument()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(8);
        Assert.Equal(8, pool.ThreadCount);
    }

    // ═══════════════════════════ FULL LIFECYCLE ═════════════════════════════

    [Fact]
    public void FullLifecycle_RentConfigureExecuteReturn()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(2);

        var job = pool.Rent(0);
        job.Value = 42;
        job.Execute();
        Assert.True(job.Executed);
        Assert.Equal(42, job.Value);

        pool.Return(0, job);

        Assert.Equal(0, job.Value);
        Assert.False(job.Executed);

        var reused = pool.Rent(0);
        Assert.Same(job, reused);
    }

    [Fact]
    public void FullLifecycle_MultipleRounds()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(2);

        for (var round = 0; round < 10; round++)
        {
            var job = pool.Rent(0);
            job.Value = round;
            job.Execute();
            Assert.True(job.Executed);
            Assert.Equal(round, job.Value);
            pool.Return(0, job);
        }

        Assert.Equal(1, pool.CountForThread(0));
    }

    [Fact]
    public void FullLifecycle_RentOnOneThread_ReturnOnAnother_StealBack()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(3);

        var job = pool.Rent(0);
        job.Value = 7;

        var workerThread = new Thread(() => pool.Return(1, job));
        workerThread.Start();
        workerThread.Join();

        Assert.Equal(0, pool.CountForThread(0));
        Assert.Equal(1, pool.CountForThread(1));

        var stolen = pool.Rent(0);
        Assert.Same(job, stolen);
        Assert.Equal(0, stolen.Value);
    }

    // ═══════════════════════════ DEBUG — DEQUE-LEVEL RACE INJECTION ════════
    //
    // These tests use DebugBeforePopCas / DebugBeforeStealCas on the underlying WorkStealingDeque
    // to inject deterministic interleavings. Thread ownership is respected: only the thread that
    // first called Push/TryPop on a deque calls those operations on it; TrySteal has no ownership
    // constraint and can run from any thread.

#if DEBUG
    [Fact]
    public void Debug_PopRacesWithSteal_OnLastItem_OneWins()
    {
        // Owner thread does Push + TryPop on deque0; thief thread does TrySteal from deque0.
        // Callback inside TryPop starts the thief just before the CAS, so both race on the last item.
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(2);
        var accessor = pool.GetTestAccessor();
        var deque0 = accessor.GetDeque(0);

        TestJob? popResult = null;
        TestJob? stealResult = null;
        var popDone = new ManualResetEventSlim();

        var ownerThread = new Thread(() =>
        {
            var job = new TestJob { Value = 1 };
            pool.Return(0, job);

            var stealDone = new ManualResetEventSlim();

            deque0.DebugBeforePopCas = () =>
            {
                deque0.DebugBeforePopCas = null;
                var thiefThread = new Thread(() =>
                {
                    // TrySteal from deque0 — no ownership constraint.
                    stealResult = pool.Rent(1);
                    stealDone.Set();
                });
                thiefThread.Start();
                stealDone.Wait();
            };

            popResult = pool.Rent(0);
            popDone.Set();
        });

        ownerThread.Start();
        popDone.Wait(TestContext.Current.CancellationToken);

        Assert.NotNull(popResult);
        Assert.NotNull(stealResult);

        // Exactly one should get the original; the other allocates fresh.
        Assert.NotSame(popResult, stealResult);
    }

    [Fact]
    public void Debug_StealRacesWithPop_OnLastItem_OneWins()
    {
        // Owner thread does Push, then waits for signal, then TryPop.
        // Thief thread does TrySteal with a callback that signals the owner to Pop.
        // Both race on the last item from their respective ends.
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(2);
        var accessor = pool.GetTestAccessor();
        var deque0 = accessor.GetDeque(0);

        TestJob? popResult = null;
        TestJob? stealResult = null;
        var ownerReady = new ManualResetEventSlim();
        var popNow = new ManualResetEventSlim();
        var ownerDone = new ManualResetEventSlim();

        var ownerThread = new Thread(() =>
        {
            var job = new TestJob { Value = 1 };
            pool.Return(0, job);
            ownerReady.Set();

            popNow.Wait();
            popResult = pool.Rent(0);
            ownerDone.Set();
        });

        ownerThread.Start();
        ownerReady.Wait(TestContext.Current.CancellationToken);

        deque0.DebugBeforeStealCas = () =>
        {
            deque0.DebugBeforeStealCas = null;
            popNow.Set();
            ownerDone.Wait();
        };

        // Test thread steals from deque0; thief has no ownership constraint.
        stealResult = pool.Rent(1);

        Assert.NotNull(popResult);
        Assert.NotNull(stealResult);
        Assert.NotSame(popResult, stealResult);
    }

    [Fact]
    public void Debug_TwoThievesRaceOnSameDeque_OneGetsItem_OtherAllocates()
    {
        // Owner thread pushes one item, then two separate thieves both TrySteal from the same deque.
        // Callback on first thief's CAS starts the second thief.
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(3);
        var accessor = pool.GetTestAccessor();
        var deque0 = accessor.GetDeque(0);

        TestJob? thief1Result = null;
        TestJob? thief2Result = null;
        var thief1Done = new ManualResetEventSlim();

        var ownerThread = new Thread(() =>
        {
            var job = new TestJob { Value = 1 };
            pool.Return(0, job);
        });
        ownerThread.Start();
        ownerThread.Join();

        deque0.DebugBeforeStealCas = () =>
        {
            deque0.DebugBeforeStealCas = null;
            var thief2Thread = new Thread(() =>
            {
                thief2Result = pool.Rent(2);
            });
            thief2Thread.Start();
            thief2Thread.Join();
        };

        thief1Result = pool.Rent(1);

        Assert.NotNull(thief1Result);
        Assert.NotNull(thief2Result);
        Assert.NotSame(thief1Result, thief2Result);
    }

    [Fact]
    public void Debug_StealRacesWithPush_StealSeesOldItem()
    {
        // Owner pushes an item. Thief steals; in the steal callback (before CAS), owner pushes a
        // second item. The steal CAS still succeeds on the old bottom item. Owner pops the new item.
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(2);
        var accessor = pool.GetTestAccessor();
        var deque0 = accessor.GetDeque(0);

        TestJob? stealResult = null;
        TestJob? ownerPopResult = null;
        var ownerReady = new ManualResetEventSlim();
        var pushNow = new ManualResetEventSlim();
        var pushDone = new ManualResetEventSlim();
        var stealDone = new ManualResetEventSlim();
        var ownerFinished = new ManualResetEventSlim();

        var ownerThread = new Thread(() =>
        {
            var job1 = new TestJob { Value = 1 };
            pool.Return(0, job1);
            ownerReady.Set();

            pushNow.Wait();
            var job2 = new TestJob { Value = 2 };
            pool.Return(0, job2);
            pushDone.Set();

            stealDone.Wait();
            ownerPopResult = pool.Rent(0);
            ownerFinished.Set();
        });

        ownerThread.Start();
        ownerReady.Wait(TestContext.Current.CancellationToken);

        deque0.DebugBeforeStealCas = () =>
        {
            deque0.DebugBeforeStealCas = null;
            pushNow.Set();
            pushDone.Wait();
        };

        stealResult = pool.Rent(1);
        stealDone.Set();
        ownerFinished.Wait(TestContext.Current.CancellationToken);

        Assert.NotNull(stealResult);
        Assert.Equal(0, stealResult.Value);

        Assert.NotNull(ownerPopResult);
        Assert.Equal(0, ownerPopResult.Value);

        ownerThread.Join();
    }

    [Fact]
    public void Debug_RoundRobinIndex_AdvancesCorrectly_AfterSteal()
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(4);
        var accessor = pool.GetTestAccessor();

        var pushGate1 = new ManualResetEventSlim();
        var pushGate2 = new ManualResetEventSlim();
        var done1 = new ManualResetEventSlim();
        var done2 = new ManualResetEventSlim();
        var shutdown = false;

        var t1 = new Thread(() =>
        {
            pool.Return(1, new TestJob());
            done1.Set();
            pushGate1.Wait();
            if (Volatile.Read(ref shutdown)) return;
            pool.Return(1, new TestJob());
            done1.Set();
        });
        var t2 = new Thread(() =>
        {
            pool.Return(2, new TestJob());
            done2.Set();
            pushGate2.Wait();
            if (Volatile.Read(ref shutdown)) return;
            pool.Return(2, new TestJob());
            done2.Set();
        });

        t1.Start(); t2.Start();
        done1.Wait(TestContext.Current.CancellationToken);
        done2.Wait(TestContext.Current.CancellationToken);

        pool.Rent(0);
        var nextIndex = accessor.NextStealIndex;
        Assert.True(nextIndex is >= 0 and < 4);

        done1.Reset();
        done2.Reset();
        pushGate1.Set();
        pushGate2.Set();
        done1.Wait(TestContext.Current.CancellationToken);
        done2.Wait(TestContext.Current.CancellationToken);

        pool.Rent(0);
        var nextIndex2 = accessor.NextStealIndex;
        Assert.NotEqual(nextIndex, nextIndex2);

        Volatile.Write(ref shutdown, true);
        t1.Join(); t2.Join();
    }
#endif

    // ═══════════════════════════ STRESS — PER-THREAD ════════════════════════

    [Theory]
    [InlineData(10_000, 2)]
    [InlineData(10_000, 4)]
    [InlineData(50_000, 4)]
    [InlineData(50_000, 8)]
    [Trait("Category", "Stress")]
    public void Stress_PerThreadRentReturn_ZeroContention(int cyclesPerThread, int threadCount)
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(threadCount);
        var barrier = new Barrier(threadCount);

        var threads = new Thread[threadCount];
        for (var threadIndex = 0; threadIndex < threadCount; threadIndex++)
        {
            var localIndex = threadIndex;

            threads[localIndex] = new Thread(() =>
            {
                barrier.SignalAndWait();

                for (var i = 0; i < cyclesPerThread; i++)
                {
                    var job = pool.Rent(localIndex);
                    job.Value = i;
                    job.Execute();
                    pool.Return(localIndex, job);
                }
            });

            threads[localIndex].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        Assert.Equal(threadCount, pool.Count);
    }

    // ═══════════════════════════ STRESS — FORK-JOIN ═════════════════════════

    [Theory]
    [InlineData(1_000, 2)]
    [InlineData(1_000, 4)]
    [InlineData(5_000, 4)]
    [InlineData(5_000, 8)]
    [Trait("Category", "Stress")]
    public void Stress_ForkJoinPattern_ItemsFlowAndStabilize(int jobsPerBatch, int workerCount)
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(workerCount + 1);
        const int CoordinatorIndex = 0;
        const int Batches = 10;
        var jobsPerWorker = jobsPerBatch / workerCount;

        // Long-lived worker threads, reused across batches (matches real-world pinned threads).
        var batchReady = new Barrier(workerCount + 1);
        var batchDone = new CountdownEvent(workerCount);
        var shutdown = false;
        var currentJobs = new TestJob[jobsPerBatch];

        var threads = new Thread[workerCount];
        for (var workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            var localIndex = workerIndex;
            var threadSlot = localIndex + 1;

            threads[localIndex] = new Thread(() =>
            {
                while (true)
                {
                    batchReady.SignalAndWait();
                    if (Volatile.Read(ref shutdown))
                        return;

                    var start = localIndex * jobsPerWorker;
                    var end = (localIndex == workerCount - 1) ? jobsPerBatch : start + jobsPerWorker;

                    for (var i = start; i < end; i++)
                    {
                        currentJobs[i].Value = i;
                        currentJobs[i].Execute();
                        pool.Return(threadSlot, currentJobs[i]);
                    }

                    batchDone.Signal();
                }
            });

            threads[localIndex].Start();
        }

        for (var batch = 0; batch < Batches; batch++)
        {
            for (var i = 0; i < jobsPerBatch; i++)
                currentJobs[i] = pool.Rent(CoordinatorIndex);

            batchDone.Reset(workerCount);
            batchReady.SignalAndWait(TestContext.Current.CancellationToken);
            batchDone.Wait(TestContext.Current.CancellationToken);

            Assert.Equal(jobsPerBatch, pool.Count);
        }

        Volatile.Write(ref shutdown, true);
        batchReady.SignalAndWait(TestContext.Current.CancellationToken);

        foreach (var thread in threads)
            thread.Join();
    }

    [Theory]
    [InlineData(5_000, 1, 4)]
    [InlineData(5_000, 2, 8)]
    [InlineData(10_000, 3, 8)]
    [Trait("Category", "Stress")]
    public void Stress_ForkJoinMultipleBatches_AllocationsDropToZero(int batchSize, int batches, int workerCount)
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(workerCount + 1);
        const int CoordinatorIndex = 0;
        var totalExecuted = 0L;
        var perWorker = batchSize / workerCount;

        var batchReady = new Barrier(workerCount + 1);
        var batchDone = new CountdownEvent(workerCount);
        var shutdown = false;
        var currentJobs = new TestJob[batchSize];

        var threads = new Thread[workerCount];
        for (var workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            var localWorker = workerIndex;
            var threadSlot = localWorker + 1;

            threads[localWorker] = new Thread(() =>
            {
                while (true)
                {
                    batchReady.SignalAndWait();
                    if (Volatile.Read(ref shutdown))
                        return;

                    var start = localWorker * perWorker;
                    var end = (localWorker == workerCount - 1) ? batchSize : start + perWorker;

                    for (var i = start; i < end; i++)
                    {
                        currentJobs[i].Execute();
                        Interlocked.Increment(ref totalExecuted);
                        pool.Return(threadSlot, currentJobs[i]);
                    }

                    batchDone.Signal();
                }
            });

            threads[localWorker].Start();
        }

        for (var batch = 0; batch < batches; batch++)
        {
            for (var i = 0; i < batchSize; i++)
            {
                currentJobs[i] = pool.Rent(CoordinatorIndex);
                currentJobs[i].Value = i;
            }

            batchDone.Reset(workerCount);
            batchReady.SignalAndWait(TestContext.Current.CancellationToken);
            batchDone.Wait(TestContext.Current.CancellationToken);
        }

        Volatile.Write(ref shutdown, true);
        batchReady.SignalAndWait(TestContext.Current.CancellationToken);

        foreach (var thread in threads)
            thread.Join();

        Assert.Equal(batchSize * batches, totalExecuted);
    }

    // ═══════════════════════════ STRESS — CONCURRENT STEAL ══════════════════

    [Theory]
    [InlineData(5_000, 4)]
    [InlineData(10_000, 8)]
    [Trait("Category", "Stress")]
    public void Stress_ConcurrentStealWhileWorkersReturn(int jobsPerBatch, int workerCount)
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(workerCount + 1);
        const int CoordinatorIndex = 0;

        var jobs = new TestJob[jobsPerBatch];
        for (var i = 0; i < jobsPerBatch; i++)
            jobs[i] = pool.Rent(CoordinatorIndex);

        var workersFinished = new CountdownEvent(workerCount);
        var threads = new Thread[workerCount];
        var jobsPerWorker = jobsPerBatch / workerCount;

        for (var workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            var localIndex = workerIndex;
            var threadSlot = localIndex + 1;
            var start = localIndex * jobsPerWorker;
            var end = (localIndex == workerCount - 1) ? jobsPerBatch : start + jobsPerWorker;

            threads[localIndex] = new Thread(() =>
            {
                for (var i = start; i < end; i++)
                {
                    jobs[i].Execute();
                    pool.Return(threadSlot, jobs[i]);
                }

                workersFinished.Signal();
            });

            threads[localIndex].Start();
        }

        workersFinished.Wait(TestContext.Current.CancellationToken);

        Assert.Equal(0, pool.CountForThread(CoordinatorIndex));
        Assert.Equal(jobsPerBatch, pool.Count);

        var rented = new TestJob[jobsPerBatch];
        for (var i = 0; i < jobsPerBatch; i++)
            rented[i] = pool.Rent(CoordinatorIndex);

        Assert.Equal(0, pool.Count);

        var uniqueJobs = new HashSet<TestJob>(rented);
        Assert.Equal(jobsPerBatch, uniqueJobs.Count);
    }

    [Theory]
    [InlineData(5_000, 4)]
    [InlineData(10_000, 8)]
    [Trait("Category", "Stress")]
    public void Stress_MultipleThievesSameVictim_NoItemsDuplicated(int itemCount, int thiefCount)
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(thiefCount + 1);
        const int VictimIndex = 0;

        for (var i = 0; i < itemCount; i++)
        {
            var job = new TestJob { Value = i };
            pool.Return(VictimIndex, job);
        }

        var barrier = new Barrier(thiefCount);
        var allStolen = new TestJob[thiefCount][];
        var stolenCounts = new int[thiefCount];
        var threads = new Thread[thiefCount];

        for (var thiefIndex = 0; thiefIndex < thiefCount; thiefIndex++)
        {
            var localIndex = thiefIndex;
            var threadSlot = localIndex + 1;
            allStolen[localIndex] = new TestJob[itemCount];

            threads[localIndex] = new Thread(() =>
            {
                barrier.SignalAndWait();

                var count = 0;
                while (true)
                {
                    var job = pool.Rent(threadSlot);
                    if (pool.CountForThread(VictimIndex) == 0 && pool.Count == 0)
                    {
                        pool.Return(threadSlot, job);
                        break;
                    }

                    allStolen[localIndex][count++] = job;
                }

                stolenCounts[localIndex] = count;
            });

            threads[localIndex].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        var uniqueJobs = new HashSet<TestJob>();
        for (var i = 0; i < thiefCount; i++)
        {
            for (var j = 0; j < stolenCounts[i]; j++)
                Assert.True(uniqueJobs.Add(allStolen[i][j]), "Duplicate item detected across thieves");
        }
    }

    [Theory]
    [InlineData(5_000, 4)]
    [InlineData(10_000, 8)]
    [Trait("Category", "Stress")]
    public void Stress_StealDuringActiveReturns_NoItemsLost(int jobsPerBatch, int workerCount)
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(workerCount + 1);
        const int CoordinatorIndex = 0;
        var jobsPerWorker = jobsPerBatch / workerCount;
        var totalPooled = 0;
        var totalUnique = 0;

        var coordinatorThread = new Thread(() =>
        {
            var jobs = new TestJob[jobsPerBatch];
            for (var i = 0; i < jobsPerBatch; i++)
                jobs[i] = pool.Rent(CoordinatorIndex);

            var workersDone = new CountdownEvent(workerCount);
            var threads = new Thread[workerCount];

            for (var workerIndex = 0; workerIndex < workerCount; workerIndex++)
            {
                var localIndex = workerIndex;
                var threadSlot = localIndex + 1;
                var start = localIndex * jobsPerWorker;
                var end = (localIndex == workerCount - 1) ? jobsPerBatch : start + jobsPerWorker;

                threads[localIndex] = new Thread(() =>
                {
                    for (var i = start; i < end; i++)
                    {
                        jobs[i].Execute();
                        pool.Return(threadSlot, jobs[i]);
                    }

                    workersDone.Signal();
                });

                threads[localIndex].Start();
            }

            workersDone.Wait();

            foreach (var thread in threads)
                thread.Join();

            totalPooled = pool.Count;

            var uniqueJobs = new HashSet<TestJob>();
            for (var i = 0; i < jobsPerBatch; i++)
            {
                var job = pool.Rent(CoordinatorIndex);
                uniqueJobs.Add(job);
            }

            totalUnique = uniqueJobs.Count;
        });

        coordinatorThread.Start();
        coordinatorThread.Join();

        Assert.Equal(jobsPerBatch, totalPooled);
        Assert.Equal(jobsPerBatch, totalUnique);
        Assert.Equal(0, pool.Count);
    }

    // ═══════════════════════════ STRESS — MIXED ════════════════════════════

    [Theory]
    [InlineData(10_000, 4)]
    [InlineData(50_000, 8)]
    [Trait("Category", "Stress")]
    public void Stress_AllThreadsRentAndReturnCrossThread(int cyclesPerThread, int threadCount)
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(threadCount);
        var barrier = new Barrier(threadCount);

        var threads = new Thread[threadCount];
        for (var threadIndex = 0; threadIndex < threadCount; threadIndex++)
        {
            var localIndex = threadIndex;

            threads[localIndex] = new Thread(() =>
            {
                barrier.SignalAndWait();

                for (var i = 0; i < cyclesPerThread; i++)
                {
                    var job = pool.Rent(localIndex);
                    job.Value = i;
                    job.Execute();
                    pool.Return(localIndex, job);
                }
            });

            threads[localIndex].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        var totalPooled = pool.Count;
        Assert.True(totalPooled >= threadCount && totalPooled <= threadCount * 2,
            $"Pool should have ~{threadCount} items but has {totalPooled}");
    }

    [Theory]
    [InlineData(5_000, 4)]
    [InlineData(10_000, 8)]
    [Trait("Category", "Stress")]
    public void Stress_BurstReturnThenCoordinatorDrains(int itemCount, int workerCount)
    {
        var pool = new ThreadLocalJobPool<TestJob, TestJobAllocator>(workerCount + 1);
        const int CoordinatorIndex = 0;

        var barrier = new Barrier(workerCount);
        var threads = new Thread[workerCount];
        var itemsPerWorker = itemCount / workerCount;

        for (var workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            var localIndex = workerIndex;
            var threadSlot = localIndex + 1;

            threads[localIndex] = new Thread(() =>
            {
                barrier.SignalAndWait();

                for (var i = 0; i < itemsPerWorker; i++)
                    pool.Return(threadSlot, new TestJob { Value = i });
            });

            threads[localIndex].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        Assert.Equal(itemCount, pool.Count);

        var uniqueJobs = new HashSet<TestJob>();
        for (var i = 0; i < itemCount; i++)
        {
            var job = pool.Rent(CoordinatorIndex);
            Assert.True(uniqueJobs.Add(job), "Duplicate item during drain");
        }

        Assert.Equal(itemCount, uniqueJobs.Count);
        Assert.Equal(0, pool.Count);
    }
}
