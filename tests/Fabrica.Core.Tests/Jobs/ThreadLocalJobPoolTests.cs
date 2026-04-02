using Fabrica.Core.Jobs;
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
            this.Value = 0;
            this.Executed = false;
        }
    }

    // ═══════════════════════════ RENT / RETURN ══════════════════════════════

    [Fact]
    public void Rent_FromEmptyPool_AllocatesNewInstance()
    {
        var pool = new ThreadLocalJobPool<TestJob>(4);
        var job = pool.Rent(0);
        Assert.NotNull(job);
    }

    [Fact]
    public void Return_ThenRent_ReusesSameInstance()
    {
        var pool = new ThreadLocalJobPool<TestJob>(4);
        var job = pool.Rent(0);
        pool.Return(0, job);

        var reused = pool.Rent(0);
        Assert.Same(job, reused);
    }

    [Fact]
    public void Rent_MultipleFromEmptyPool_AllocatesDistinctInstances()
    {
        var pool = new ThreadLocalJobPool<TestJob>(4);
        var job1 = pool.Rent(0);
        var job2 = pool.Rent(0);
        Assert.NotSame(job1, job2);
    }

    [Fact]
    public void Return_MultipleThenRent_ReturnsInLifoOrder()
    {
        var pool = new ThreadLocalJobPool<TestJob>(4);
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

    // ═══════════════════════════ CROSS-THREAD SCANNING ══════════════════════

    [Fact]
    public void Rent_OwnStackEmpty_StealsFromOtherThread()
    {
        var pool = new ThreadLocalJobPool<TestJob>(4);
        var job = new TestJob();
        pool.Return(2, job);

        var stolen = pool.Rent(0);
        Assert.Same(job, stolen);
    }

    [Fact]
    public void Rent_StealsFromFirstNonEmptyThread()
    {
        var pool = new ThreadLocalJobPool<TestJob>(4);

        var job1 = new TestJob { Value = 1 };
        var job2 = new TestJob { Value = 2 };
        pool.Return(1, job1);
        pool.Return(3, job2);

        var stolen = pool.Rent(0);
        Assert.Same(job1, stolen);
    }

    [Fact]
    public void Rent_AllStacksEmpty_AllocatesNew()
    {
        var pool = new ThreadLocalJobPool<TestJob>(4);
        var job = pool.Rent(0);
        Assert.NotNull(job);
        Assert.Equal(0, pool.Count);
    }

    // ═══════════════════════════ COUNT ═══════════════════════════════════════

    [Fact]
    public void Count_EmptyPool_ReturnsZero()
    {
        var pool = new ThreadLocalJobPool<TestJob>(4);
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void Count_SumsAcrossThreads()
    {
        var pool = new ThreadLocalJobPool<TestJob>(4);
        pool.Return(0, new TestJob());
        pool.Return(0, new TestJob());
        pool.Return(2, new TestJob());
        Assert.Equal(3, pool.Count);
    }

    [Fact]
    public void CountForThread_ReturnsPerThreadCount()
    {
        var pool = new ThreadLocalJobPool<TestJob>(4);
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
        var pool = new ThreadLocalJobPool<TestJob>(8);
        Assert.Equal(8, pool.ThreadCount);
    }

    // ═══════════════════════════ JOB LIFECYCLE ══════════════════════════════

    [Fact]
    public void FullLifecycle_RentConfigureExecuteReturn()
    {
        var pool = new ThreadLocalJobPool<TestJob>(2);

        var job = pool.Rent(0);
        job.Value = 42;
        job.Execute();
        Assert.True(job.Executed);
        Assert.Equal(42, job.Value);

        job.Return();
        pool.Return(1, job);

        Assert.Equal(0, job.Value);
        Assert.False(job.Executed);

        var reused = pool.Rent(1);
        Assert.Same(job, reused);
    }

    // ═══════════════════════════ FORK-JOIN PATTERN ══════════════════════════

    [Theory]
    [InlineData(1_000, 2)]
    [InlineData(1_000, 4)]
    [InlineData(5_000, 4)]
    [InlineData(5_000, 8)]
    [Trait("Category", "Stress")]
    public void Stress_ForkJoinPattern_ItemsFlowAndStabilize(int jobsPerBatch, int threadCount)
    {
        var pool = new ThreadLocalJobPool<TestJob>(threadCount);
        const int Batches = 10;

        for (var batch = 0; batch < Batches; batch++)
        {
            var jobs = new TestJob[jobsPerBatch];
            for (var i = 0; i < jobsPerBatch; i++)
                jobs[i] = pool.Rent(0);

            var barrier = new Barrier(threadCount);
            var threads = new Thread[threadCount];
            var jobsPerThread = jobsPerBatch / threadCount;

            for (var threadIndex = 0; threadIndex < threadCount; threadIndex++)
            {
                var localIndex = threadIndex;
                var start = localIndex * jobsPerThread;
                var end = (localIndex == threadCount - 1) ? jobsPerBatch : start + jobsPerThread;

                threads[localIndex] = new Thread(() =>
                {
                    barrier.SignalAndWait();

                    for (var i = start; i < end; i++)
                    {
                        jobs[i].Value = i;
                        jobs[i].Execute();
                        jobs[i].Return();
                        pool.Return(localIndex, jobs[i]);
                    }
                });

                threads[localIndex].Start();
            }

            foreach (var thread in threads)
                thread.Join();

            Assert.Equal(jobsPerBatch, pool.Count);
        }
    }

    [Theory]
    [InlineData(10_000, 2)]
    [InlineData(10_000, 4)]
    [InlineData(50_000, 4)]
    [Trait("Category", "Stress")]
    public void Stress_PerThreadRentReturn_ZeroContention(int cyclesPerThread, int threadCount)
    {
        var pool = new ThreadLocalJobPool<TestJob>(threadCount);
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
                    job.Return();
                    pool.Return(localIndex, job);
                }
            });

            threads[localIndex].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        Assert.Equal(threadCount, pool.Count);
    }

    [Theory]
    [InlineData(5_000, 4)]
    [InlineData(10_000, 8)]
    [Trait("Category", "Stress")]
    public void Stress_CoordinatorRentsFromWorkerStacks_AfterWorkersIdle(int jobsPerBatch, int workerCount)
    {
        var pool = new ThreadLocalJobPool<TestJob>(workerCount + 1);
        const int CoordinatorIndex = 0;

        var jobs = new TestJob[jobsPerBatch];
        for (var i = 0; i < jobsPerBatch; i++)
            jobs[i] = pool.Rent(CoordinatorIndex);

        var barrier = new Barrier(workerCount);
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
                barrier.SignalAndWait();

                for (var i = start; i < end; i++)
                {
                    jobs[i].Execute();
                    jobs[i].Return();
                    pool.Return(threadSlot, jobs[i]);
                }
            });

            threads[localIndex].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        Assert.Equal(0, pool.CountForThread(CoordinatorIndex));
        Assert.Equal(jobsPerBatch, pool.Count);

        var rented = new TestJob[jobsPerBatch];
        for (var i = 0; i < jobsPerBatch; i++)
            rented[i] = pool.Rent(CoordinatorIndex);

        Assert.Equal(0, pool.Count);

        var uniqueJobs = new HashSet<TestJob>(rented);
        Assert.Equal(jobsPerBatch, uniqueJobs.Count);
    }
}
