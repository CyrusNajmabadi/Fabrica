using System.Collections.Concurrent;
using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Jobs;

public class JobPoolTests
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
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var job = pool.Rent();
        Assert.NotNull(job);
    }

    [Fact]
    public void Rent_MultipleFromEmptyPool_AllocatesDistinctInstances()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var job1 = pool.Rent();
        var job2 = pool.Rent();
        var job3 = pool.Rent();
        Assert.NotSame(job1, job2);
        Assert.NotSame(job2, job3);
        Assert.NotSame(job1, job3);
    }

    [Fact]
    public void Rent_FromEmptyPool_ReturnsCleanInstance()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var job = pool.Rent();
        Assert.Equal(0, job.Value);
        Assert.False(job.Executed);
    }

    // ═══════════════════════════ RENT — POPULATED POOL ══════════════════════

    [Fact]
    public void Rent_AfterReturn_ReusesSameInstance()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var job = pool.Rent();
        pool.Return(job);

        var reused = pool.Rent();
        Assert.Same(job, reused);
    }

    [Fact]
    public void Rent_MultipleThenReturn_ReturnsInLifoOrder()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var job1 = pool.Rent();
        var job2 = pool.Rent();
        var job3 = pool.Rent();

        pool.Return(job1);
        pool.Return(job2);
        pool.Return(job3);

        Assert.Same(job3, pool.Rent());
        Assert.Same(job2, pool.Rent());
        Assert.Same(job1, pool.Rent());
    }

    [Fact]
    public void Rent_DrainsPoolThenAllocates()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var job1 = pool.Rent();
        pool.Return(job1);

        var reused = pool.Rent();
        Assert.Same(job1, reused);

        var fresh = pool.Rent();
        Assert.NotSame(job1, fresh);
    }

    // ═══════════════════════════ RETURN — RESET ═════════════════════════════

    [Fact]
    public void Return_ResetsFieldsViaAllocator()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var job = pool.Rent();
        job.Value = 99;
        job.Executed = true;

        pool.Return(job);

        var reused = pool.Rent();
        Assert.Same(job, reused);
        Assert.Equal(0, reused.Value);
        Assert.False(reused.Executed);
    }

    [Fact]
    public void Return_ResetsBeforePooling_NotOnNextRent()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var job = pool.Rent();
        job.Value = 42;

        pool.Return(job);

        Assert.Equal(0, job.Value);
    }

    // ═══════════════════════════ POOL NEXT ═══════════════════════════════════

    [Fact]
    public void PoolNext_IsClearedAfterRent()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var job = pool.Rent();
        pool.Return(job);

        var rented = pool.Rent();
        Assert.Null(rented._poolNext);
    }

    [Fact]
    public void PoolNext_ChainedCorrectlyInPool()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var accessor = pool.GetTestAccessor();
        var job1 = pool.Rent();
        var job2 = pool.Rent();

        pool.Return(job1);
        pool.Return(job2);

        Assert.Same(job2, accessor.Head);
        Assert.Same(job1, accessor.Head!._poolNext);
    }

    // ═══════════════════════════ COUNT ═══════════════════════════════════════

    [Fact]
    public void Count_EmptyPool_ReturnsZero()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void Count_AfterReturns_ReflectsPoolSize()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var job1 = pool.Rent();
        var job2 = pool.Rent();
        pool.Return(job1);
        pool.Return(job2);
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void Count_AfterRentAndReturn_TracksCorrectly()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var job = pool.Rent();
        Assert.Equal(0, pool.Count);

        pool.Return(job);
        Assert.Equal(1, pool.Count);

        pool.Rent();
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void Count_AfterDrain_ReturnsZero()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var jobs = new TestJob[5];
        for (var i = 0; i < 5; i++)
            jobs[i] = pool.Rent();

        for (var i = 0; i < 5; i++)
            pool.Return(jobs[i]);

        Assert.Equal(5, pool.Count);

        for (var i = 0; i < 5; i++)
            pool.Rent();

        Assert.Equal(0, pool.Count);
    }

    // ═══════════════════════════ FULL LIFECYCLE ═════════════════════════════

    [Fact]
    public void FullLifecycle_RentConfigureExecuteReturn()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();

        var job = pool.Rent();
        job.Value = 42;
        job.Execute();
        Assert.True(job.Executed);
        Assert.Equal(42, job.Value);

        pool.Return(job);

        var reused = pool.Rent();
        Assert.Same(job, reused);
        Assert.False(reused.Executed);
        Assert.Equal(0, reused.Value);
    }

    [Fact]
    public void FullLifecycle_MultipleRounds()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();

        for (var round = 0; round < 10; round++)
        {
            var job = pool.Rent();
            job.Value = round;
            job.Execute();
            Assert.True(job.Executed);
            Assert.Equal(round, job.Value);
            pool.Return(job);
        }

        Assert.Equal(1, pool.Count);
    }

    // ═══════════════════════════ DEBUG — RENT CAS RACES ═════════════════════

#if DEBUG
    [Fact]
    public void Debug_RentCasLostToConcurrentRent_RetriesSuccessfully()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var job1 = pool.Rent();
        var job2 = pool.Rent();
        pool.Return(job1);
        pool.Return(job2);

        TestJob? stolen = null;

        pool.DebugBeforeRentCas = () =>
        {
            pool.DebugBeforeRentCas = null;
            stolen = pool.Rent();
        };

        var rented = pool.Rent();

        Assert.NotNull(stolen);
        Assert.NotNull(rented);
        Assert.NotSame(stolen, rented);

        var allItems = new HashSet<TestJob> { stolen, rented };
        Assert.Contains(job1, allItems);
        Assert.Contains(job2, allItems);
    }

    [Fact]
    public void Debug_RentCasLostOnLastItem_FallsToAllocate()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var job = pool.Rent();
        pool.Return(job);

        TestJob? stolen = null;

        pool.DebugBeforeRentCas = () =>
        {
            pool.DebugBeforeRentCas = null;
            stolen = pool.Rent();
        };

        var rented = pool.Rent();

        Assert.Same(job, stolen);
        Assert.NotSame(job, rented);
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void Debug_RentCasLostToConcurrentReturn_RetriesAndGetsNewHead()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var job1 = pool.Rent();
        var job2 = pool.Rent();
        pool.Return(job1);

        pool.DebugBeforeRentCas = () =>
        {
            pool.DebugBeforeRentCas = null;
            pool.Return(job2);
        };

        var rented = pool.Rent();

        Assert.True(rented == job1 || rented == job2);
    }

    [Fact]
    public void Debug_ReturnCasLostToConcurrentReturn_RetriesSuccessfully()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var job1 = pool.Rent();
        var job2 = pool.Rent();

        pool.DebugBeforeReturnCas = () =>
        {
            pool.DebugBeforeReturnCas = null;
            pool.Return(job2);
        };

        pool.Return(job1);

        Assert.Equal(2, pool.Count);

        var r1 = pool.Rent();
        var r2 = pool.Rent();
        var allItems = new HashSet<TestJob> { r1, r2 };
        Assert.Contains(job1, allItems);
        Assert.Contains(job2, allItems);
    }

    [Fact]
    public void Debug_ReturnCasLostToConcurrentRent_RetriesSuccessfully()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var job1 = pool.Rent();
        var job2 = pool.Rent();
        pool.Return(job1);

        TestJob? stolen = null;

        pool.DebugBeforeReturnCas = () =>
        {
            pool.DebugBeforeReturnCas = null;
            stolen = pool.Rent();
        };

        pool.Return(job2);

        Assert.Same(job1, stolen);
        Assert.Equal(1, pool.Count);

        var rented = pool.Rent();
        Assert.Same(job2, rented);
    }

    [Fact]
    public void Debug_MultipleCasFailures_EventuallySucceeds()
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var jobs = new TestJob[5];
        for (var i = 0; i < 5; i++)
        {
            jobs[i] = pool.Rent();
            pool.Return(jobs[i]);
        }

        var failures = 0;

        pool.DebugBeforeRentCas = () =>
        {
            failures++;
            if (failures < 3)
                pool.Return(pool.Rent());
            else
                pool.DebugBeforeRentCas = null;
        };

        var rented = pool.Rent();
        Assert.NotNull(rented);
        Assert.True(failures >= 3);
    }
#endif

    // ═══════════════════════════ STRESS — CONCURRENT RENT/RETURN ════════════

    [Theory]
    [InlineData(10_000, 2)]
    [InlineData(10_000, 4)]
    [InlineData(50_000, 4)]
    [InlineData(50_000, 8)]
    [InlineData(100_000, 8)]
    [Trait("Category", "Stress")]
    public void Stress_ConcurrentRentAndReturn_NoItemsLostOrDuplicated(int itemsPerThread, int threadCount)
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var barrier = new Barrier(threadCount);
        var allJobs = new TestJob[threadCount][];

        var threads = new Thread[threadCount];
        for (var threadIndex = 0; threadIndex < threadCount; threadIndex++)
        {
            var localIndex = threadIndex;
            allJobs[localIndex] = new TestJob[itemsPerThread];

            threads[localIndex] = new Thread(() =>
            {
                barrier.SignalAndWait();

                for (var i = 0; i < itemsPerThread; i++)
                {
                    var job = pool.Rent();
                    job.Value = (localIndex * itemsPerThread) + i;
                    allJobs[localIndex][i] = job;
                }

                barrier.SignalAndWait();

                for (var i = 0; i < itemsPerThread; i++)
                    pool.Return(allJobs[localIndex][i]);
            });

            threads[localIndex].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        var uniqueJobs = new HashSet<TestJob>();
        foreach (var batch in allJobs)
        {
            foreach (var job in batch)
                Assert.True(uniqueJobs.Add(job), "Duplicate job instance detected");
        }

        Assert.Equal(threadCount * itemsPerThread, uniqueJobs.Count);
        Assert.Equal(threadCount * itemsPerThread, pool.Count);
    }

    [Theory]
    [InlineData(10_000, 2)]
    [InlineData(10_000, 4)]
    [InlineData(50_000, 4)]
    [InlineData(50_000, 8)]
    [Trait("Category", "Stress")]
    public void Stress_InterleavedRentReturn_PoolStabilizes(int cyclesPerThread, int threadCount)
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var barrier = new Barrier(threadCount);

        var threads = new Thread[threadCount];
        for (var threadIndex = 0; threadIndex < threadCount; threadIndex++)
        {
            threads[threadIndex] = new Thread(() =>
            {
                barrier.SignalAndWait();

                for (var i = 0; i < cyclesPerThread; i++)
                {
                    var job = pool.Rent();
                    job.Value = i;
                    job.Execute();
                    pool.Return(job);
                }
            });

            threads[threadIndex].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        Assert.True(pool.Count <= threadCount, $"Pool should stabilize at <= {threadCount} items, but has {pool.Count}");
    }

    [Theory]
    [InlineData(50_000, 1, 4)]
    [InlineData(50_000, 2, 4)]
    [InlineData(100_000, 1, 8)]
    [Trait("Category", "Stress")]
    public void Stress_OneProducerManyConsumers_NoItemsLost(int itemCount, int producerCount, int consumerCount)
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var jobs = new ConcurrentBag<TestJob>();
        var consumerBarrier = new CountdownEvent(consumerCount);

        var producers = new Thread[producerCount];
        for (var producerIndex = 0; producerIndex < producerCount; producerIndex++)
        {
            var perProducer = itemCount / producerCount;
            producers[producerIndex] = new Thread(() =>
            {
                for (var i = 0; i < perProducer; i++)
                {
                    var job = pool.Rent();
                    job.Value = i;
                    jobs.Add(job);
                }
            });

            producers[producerIndex].Start();
        }

        foreach (var producer in producers)
            producer.Join();

        var consumers = new Thread[consumerCount];
        for (var consumerIndex = 0; consumerIndex < consumerCount; consumerIndex++)
        {
            consumers[consumerIndex] = new Thread(() =>
            {
                while (jobs.TryTake(out var job))
                {
                    job.Execute();
                    pool.Return(job);
                }

                consumerBarrier.Signal();
            });

            consumers[consumerIndex].Start();
        }

        consumerBarrier.Wait(TestContext.Current.CancellationToken);

        Assert.Equal(itemCount, pool.Count);
    }

    [Theory]
    [InlineData(10_000, 4)]
    [InlineData(50_000, 8)]
    [Trait("Category", "Stress")]
    public void Stress_BurstReturn_AllItemsPooled(int itemCount, int threadCount)
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var jobs = new TestJob[itemCount];
        for (var i = 0; i < itemCount; i++)
            jobs[i] = pool.Rent();

        var barrier = new Barrier(threadCount);
        var threads = new Thread[threadCount];
        var itemsPerThread = itemCount / threadCount;

        for (var threadIndex = 0; threadIndex < threadCount; threadIndex++)
        {
            var start = threadIndex * itemsPerThread;
            var end = (threadIndex == threadCount - 1) ? itemCount : start + itemsPerThread;

            threads[threadIndex] = new Thread(() =>
            {
                barrier.SignalAndWait();

                for (var i = start; i < end; i++)
                    pool.Return(jobs[i]);
            });

            threads[threadIndex].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        Assert.Equal(itemCount, pool.Count);
    }

    [Theory]
    [InlineData(10_000, 4)]
    [InlineData(50_000, 8)]
    [Trait("Category", "Stress")]
    public void Stress_SimultaneousRentFromPopulatedPool_NoItemsDuplicated(int itemCount, int threadCount)
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        for (var i = 0; i < itemCount; i++)
            pool.Return(pool.Rent());

        var barrier = new Barrier(threadCount);
        var allRented = new ConcurrentBag<TestJob>();
        var threads = new Thread[threadCount];

        for (var threadIndex = 0; threadIndex < threadCount; threadIndex++)
        {
            threads[threadIndex] = new Thread(() =>
            {
                barrier.SignalAndWait();

                for (var i = 0; i < itemCount / threadCount; i++)
                    allRented.Add(pool.Rent());
            });

            threads[threadIndex].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        var uniqueJobs = new HashSet<TestJob>(allRented);
        Assert.Equal(allRented.Count, uniqueJobs.Count);
    }

    [Theory]
    [InlineData(10_000, 2)]
    [InlineData(10_000, 4)]
    [InlineData(50_000, 8)]
    [Trait("Category", "Stress")]
    public void Stress_HalfRentHalfReturn_NoCorruption(int cyclesPerThread, int threadCount)
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();

        for (var i = 0; i < threadCount * 10; i++)
            pool.Return(pool.Rent());

        var barrier = new Barrier(threadCount);
        var threads = new Thread[threadCount];

        for (var threadIndex = 0; threadIndex < threadCount; threadIndex++)
        {
            var isRenter = threadIndex % 2 == 0;

            threads[threadIndex] = new Thread(() =>
            {
                barrier.SignalAndWait();

                if (isRenter)
                {
                    for (var i = 0; i < cyclesPerThread; i++)
                    {
                        var job = pool.Rent();
                        pool.Return(job);
                    }
                }
                else
                {
                    for (var i = 0; i < cyclesPerThread; i++)
                    {
                        var job = pool.Rent();
                        job.Value = i;
                        pool.Return(job);
                    }
                }
            });

            threads[threadIndex].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        Assert.True(pool.Count >= 0);
        Assert.True(pool.Count <= (threadCount * 10) + threadCount);
    }

    [Theory]
    [InlineData(5_000, 1, 8)]
    [InlineData(10_000, 2, 8)]
    [Trait("Category", "Stress")]
    public void Stress_ForkJoinPattern_CoordinatorRentsWorkersReturn(int batchSize, int batches, int workerCount)
    {
        var pool = new JobPool<TestJob, TestJobAllocator>();
        var totalExecuted = 0L;

        for (var batch = 0; batch < batches; batch++)
        {
            var jobs = new TestJob[batchSize];
            for (var i = 0; i < batchSize; i++)
            {
                jobs[i] = pool.Rent();
                jobs[i].Value = i;
            }

            var done = new CountdownEvent(workerCount);
            var threads = new Thread[workerCount];
            var perWorker = batchSize / workerCount;

            for (var workerIndex = 0; workerIndex < workerCount; workerIndex++)
            {
                var start = workerIndex * perWorker;
                var end = (workerIndex == workerCount - 1) ? batchSize : start + perWorker;

                threads[workerIndex] = new Thread(() =>
                {
                    for (var i = start; i < end; i++)
                    {
                        jobs[i].Execute();
                        Interlocked.Increment(ref totalExecuted);
                        pool.Return(jobs[i]);
                    }

                    done.Signal();
                });

                threads[workerIndex].Start();
            }

            done.Wait(TestContext.Current.CancellationToken);
        }

        Assert.Equal(batchSize * batches, totalExecuted);
    }
}
