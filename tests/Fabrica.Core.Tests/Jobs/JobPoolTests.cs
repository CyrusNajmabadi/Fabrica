using Fabrica.Core.Jobs;
using Xunit;

namespace Fabrica.Core.Tests.Jobs;

public class JobPoolTests
{
    // ── Test job ──────────────────────────────────────────────────────────

    private sealed class TestJob : Job
    {
        public int Value { get; set; }
        public bool Executed { get; set; }

        protected internal override void Execute(WorkerContext context)
            => this.Executed = true;

        protected internal override void Reset()
        {
            this.Value = 0;
            this.Executed = false;
        }
    }

    // ── Rent — empty pool ─────────────────────────────────────────────────

    [Fact]
    public void Rent_FromEmptyPool_AllocatesNewInstance()
    {
        var pool = new JobPool<TestJob>();
        var job = pool.Rent();
        Assert.NotNull(job);
    }

    [Fact]
    public void Rent_MultipleFromEmpty_AllocatesDistinctInstances()
    {
        var pool = new JobPool<TestJob>();
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
        var pool = new JobPool<TestJob>();
        var job = pool.Rent();
        Assert.Equal(0, job.Value);
        Assert.False(job.Executed);
    }

    // ── Rent — populated pool ─────────────────────────────────────────────

    [Fact]
    public void Rent_AfterReturn_ReusesSameInstance()
    {
        var pool = new JobPool<TestJob>();
        var job = pool.Rent();
        pool.Return(job);

        var reused = pool.Rent();
        Assert.Same(job, reused);
    }

    [Fact]
    public void Rent_MultipleThenReturn_ReturnsInLifoOrder()
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
    public void Rent_DrainsPoolThenAllocates()
    {
        var pool = new JobPool<TestJob>();
        var job = pool.Rent();
        pool.Return(job);

        var reused = pool.Rent();
        Assert.Same(job, reused);

        var fresh = pool.Rent();
        Assert.NotSame(job, fresh);
    }

    // ── Return — resets state ─────────────────────────────────────────────

    [Fact]
    public void Return_ResetsSubclassState()
    {
        var pool = new JobPool<TestJob>();
        var job = pool.Rent();
        job.Value = 42;
        job.Executed = true;

        pool.Return(job);
        var reused = pool.Rent();

        Assert.Same(job, reused);
        Assert.Equal(0, reused.Value);
        Assert.False(reused.Executed);
    }

    [Fact]
    public void Return_ResetsBaseClassState()
    {
        var pool = new JobPool<TestJob>();
        var job = pool.Rent();

        var otherJob = pool.Rent();
        job.RemainingDependencies = 3;
        job.Dependents = [otherJob];

        pool.Return(job);
        var reused = pool.Rent();

        Assert.Same(job, reused);
        Assert.Equal(0, reused.RemainingDependencies);
        Assert.Null(reused.Dependents);
        Assert.Null(reused.PoolNext);
    }

    [Fact]
    public void Return_ClearsPoolNext()
    {
        var pool = new JobPool<TestJob>();
        var job1 = pool.Rent();
        var job2 = pool.Rent();

        pool.Return(job1);
        pool.Return(job2);

        var rented = pool.Rent();
        Assert.Null(rented.PoolNext);
    }

    // ── Count ─────────────────────────────────────────────────────────────

    [Fact]
    public void Count_EmptyPool_IsZero()
    {
        var pool = new JobPool<TestJob>();
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void Count_AfterReturns_ReflectsSize()
    {
        var pool = new JobPool<TestJob>();
        var job1 = pool.Rent();
        var job2 = pool.Rent();

        pool.Return(job1);
        Assert.Equal(1, pool.Count);

        pool.Return(job2);
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void Count_AfterRentAndReturn_Tracks()
    {
        var pool = new JobPool<TestJob>();
        var job = pool.Rent();
        Assert.Equal(0, pool.Count);

        pool.Return(job);
        Assert.Equal(1, pool.Count);

        pool.Rent();
        Assert.Equal(0, pool.Count);
    }

    // ── Full lifecycle ────────────────────────────────────────────────────

    [Fact]
    public void FullLifecycle_RentConfigureExecuteReturn()
    {
        var pool = new JobPool<TestJob>();

        var job = pool.Rent();
        job.Value = 99;
        job.Execute(null!);

        Assert.Equal(99, job.Value);
        Assert.True(job.Executed);

        pool.Return(job);

        var reused = pool.Rent();
        Assert.Same(job, reused);
        Assert.Equal(0, reused.Value);
        Assert.False(reused.Executed);
    }

    // ── CAS contention (deterministic interleaving) ───────────────────────

    [Fact]
    public void Rent_CasRetry_WhenConcurrentRentStealsHead()
    {
        var pool = new JobPool<TestJob>();
        var job1 = pool.Rent();
        var job2 = pool.Rent();

        pool.Return(job1);
        pool.Return(job2);

#if DEBUG
        var stolen = false;
        pool.DebugBeforeRentCas = () =>
        {
            if (!stolen)
            {
                stolen = true;
                pool.Rent();
            }
        };
#endif

        var rented = pool.Rent();
        Assert.NotNull(rented);
    }

    [Fact]
    public void Return_CasRetry_WhenConcurrentReturnChangesHead()
    {
        var pool = new JobPool<TestJob>();
        var job1 = pool.Rent();
        var job2 = pool.Rent();

#if DEBUG
        var injected = false;
        pool.DebugBeforeReturnCas = () =>
        {
            if (!injected)
            {
                injected = true;
                pool.Return(job2);
            }
        };
#endif

        pool.Return(job1);
        Assert.True(pool.Count >= 1);
    }

    // ── Concurrent stress ─────────────────────────────────────────────────

    [Fact]
    public void ConcurrentRentReturn_NoLostItems()
    {
        const int ThreadCount = 8;
        const int OpsPerThread = 1000;
        var pool = new JobPool<TestJob>();
        using var barrier = new Barrier(ThreadCount);

        var threads = new Thread[ThreadCount];
        for (var t = 0; t < ThreadCount; t++)
        {
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < OpsPerThread; i++)
                {
                    var job = pool.Rent();
                    job.Value = i;
                    pool.Return(job);
                }
            });
            threads[t].Start();
        }

        for (var t = 0; t < ThreadCount; t++)
            threads[t].Join();

        var count = pool.Count;
        Assert.True(count > 0, "Pool should have items after concurrent rent/return.");
        Assert.True(count <= ThreadCount * OpsPerThread, $"Pool count {count} should not exceed total operations.");
    }

    [Fact]
    public void ConcurrentRent_AllDistinct()
    {
        const int ThreadCount = 8;
        const int JobsPerThread = 50;
        var pool = new JobPool<TestJob>();

        for (var i = 0; i < ThreadCount * JobsPerThread; i++)
            pool.Return(new TestJob());

        var allJobs = new TestJob[ThreadCount * JobsPerThread];
        using var barrier = new Barrier(ThreadCount);

        var threads = new Thread[ThreadCount];
        for (var t = 0; t < ThreadCount; t++)
        {
            var threadIndex = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < JobsPerThread; i++)
                    allJobs[(threadIndex * JobsPerThread) + i] = pool.Rent();
            });
            threads[t].Start();
        }

        for (var t = 0; t < ThreadCount; t++)
            threads[t].Join();

        var distinct = new HashSet<TestJob>(allJobs);
        Assert.Equal(allJobs.Length, distinct.Count);
    }
}
