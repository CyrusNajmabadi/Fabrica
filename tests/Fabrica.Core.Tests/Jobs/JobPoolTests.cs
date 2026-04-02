using System.Collections.Concurrent;
using Fabrica.Core.Jobs;
using Xunit;

namespace Fabrica.Core.Tests.Jobs;

public class JobPoolTests
{
    // ═══════════════════════════ TEST JOB ════════════════════════════════════

    private sealed class TestJob : Job
    {
        public static readonly JobPool<TestJob> Pool = new();

        public int Value { get; set; }
        public bool Executed { get; set; }

        public override void Execute()
            => this.Executed = true;

        public override void Return()
        {
            this.Value = 0;
            this.Executed = false;
            Pool.Return(this);
        }
    }

    // ═══════════════════════════ RENT / RETURN ══════════════════════════════

    [Fact]
    public void Rent_FromEmptyPool_AllocatesNewInstance()
    {
        var pool = new JobPool<TestJob>();
        var job = pool.Rent();
        Assert.NotNull(job);
    }

    [Fact]
    public void Return_ThenRent_ReusesSameInstance()
    {
        var pool = new JobPool<TestJob>();
        var job = pool.Rent();
        pool.Return(job);

        var reused = pool.Rent();
        Assert.Same(job, reused);
    }

    [Fact]
    public void Rent_MultipleFromEmptyPool_AllocatesDistinctInstances()
    {
        var pool = new JobPool<TestJob>();
        var job1 = pool.Rent();
        var job2 = pool.Rent();
        Assert.NotSame(job1, job2);
    }

    [Fact]
    public void Return_MultipleThenRent_ReturnsInLifoOrder()
    {
        var pool = new JobPool<TestJob>();
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
    public void PoolNext_IsClearedAfterRent()
    {
        var pool = new JobPool<TestJob>();
        var job = pool.Rent();
        pool.Return(job);

        var rented = pool.Rent();
        Assert.Null(rented._poolNext);
    }

    // ═══════════════════════════ COUNT ═══════════════════════════════════════

    [Fact]
    public void Count_EmptyPool_ReturnsZero()
    {
        var pool = new JobPool<TestJob>();
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void Count_AfterReturns_ReflectsPoolSize()
    {
        var pool = new JobPool<TestJob>();
        var job1 = pool.Rent();
        var job2 = pool.Rent();
        pool.Return(job1);
        pool.Return(job2);
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void Count_AfterRentAndReturn_TracksCorrectly()
    {
        var pool = new JobPool<TestJob>();
        var job = pool.Rent();
        Assert.Equal(0, pool.Count);

        pool.Return(job);
        Assert.Equal(1, pool.Count);

        pool.Rent();
        Assert.Equal(0, pool.Count);
    }

    // ═══════════════════════════ JOB LIFECYCLE ══════════════════════════════

    [Fact]
    public void FullLifecycle_RentConfigureExecuteReturn()
    {
        var job = TestJob.Pool.Rent();
        job.Value = 42;

        job.Execute();
        Assert.True(job.Executed);
        Assert.Equal(42, job.Value);

        job.Return();

        var reused = TestJob.Pool.Rent();
        Assert.Same(job, reused);
        Assert.False(reused.Executed);
        Assert.Equal(0, reused.Value);
    }

    // ═══════════════════════════ CONCURRENT RENT/RETURN ═════════════════════

    [Theory]
    [InlineData(10_000, 2)]
    [InlineData(10_000, 4)]
    [InlineData(50_000, 4)]
    [InlineData(50_000, 8)]
    [Trait("Category", "Stress")]
    public void Stress_ConcurrentRentAndReturn_NoItemsLostOrDuplicated(int itemsPerThread, int threadCount)
    {
        var pool = new JobPool<TestJob>();
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
    [Trait("Category", "Stress")]
    public void Stress_InterleavedRentReturn_PoolStabilizes(int cyclesPerThread, int threadCount)
    {
        var pool = new JobPool<TestJob>();
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
        var pool = new JobPool<TestJob>();
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
}
