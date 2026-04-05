using System.Collections.Concurrent;
using Fabrica.Core.Jobs;
using Xunit;

namespace Fabrica.Core.Tests.Jobs;

public class JobSchedulerTests
{
    // ── Test job types ──────────────────────────────────────────────────────

    private sealed class TestJob : Job
    {
        public bool Executed { get; set; }
        public int ExecutedOnWorker { get; set; } = -1;
        public Action<WorkerContext>? OnExecute { get; set; }

        internal override void Execute(WorkerContext context)
        {
            this.ExecutedOnWorker = context.WorkerIndex;
            this.Executed = true;
            this.OnExecute?.Invoke(context);
        }

        internal override void Reset()
        {
            this.Executed = false;
            this.ExecutedOnWorker = -1;
            this.OnExecute = null;
        }
    }

    // ── Basic execution ─────────────────────────────────────────────────────

    [Fact]
    public void SingleJob_Executes()
    {
        using var pool = new WorkerPool(workerCount: 2);
        var scheduler = new JobScheduler(pool);

        var job = new TestJob();
        scheduler.Submit(job);
        Assert.True(scheduler.WaitForCompletion(millisecondsTimeout: 5000));

        Assert.True(job.Executed);
    }

    [Fact]
    public void SingleJob_ReceivesWorkerContext()
    {
        using var pool = new WorkerPool(workerCount: 2);
        var scheduler = new JobScheduler(pool);

        var capturedIndex = -1;
        var job = new TestJob
        {
            OnExecute = ctx => capturedIndex = ctx.WorkerIndex,
        };

        scheduler.Submit(job);
        Assert.True(scheduler.WaitForCompletion(millisecondsTimeout: 5000));

        Assert.True(capturedIndex >= 0);
    }

    // ── DAG: chain ──────────────────────────────────────────────────────────

    [Fact]
    public void Chain_ExecutesInDependencyOrder()
    {
        using var pool = new WorkerPool(workerCount: 2);
        var scheduler = new JobScheduler(pool);

        var order = new ConcurrentQueue<string>();

        var jobA = new TestJob { OnExecute = _ => order.Enqueue("A") };
        var jobB = new TestJob { _remainingDependencies = 1, OnExecute = _ => order.Enqueue("B") };
        var jobC = new TestJob { _remainingDependencies = 1, OnExecute = _ => order.Enqueue("C") };

        jobA._dependents = [jobB];
        jobB._dependents = [jobC];

        scheduler.Submit(jobA);
        Assert.True(scheduler.WaitForCompletion(millisecondsTimeout: 5000));

        Assert.Equal(["A", "B", "C"], [.. order]);
    }

    // ── DAG: fan-out / fan-in ───────────────────────────────────────────────

    [Fact]
    public void FanOutFanIn_RespectsDependencies()
    {
        using var pool = new WorkerPool(workerCount: 4);
        var scheduler = new JobScheduler(pool);

        var order = new ConcurrentQueue<string>();

        var root = new TestJob { OnExecute = _ => order.Enqueue("root") };
        var left = new TestJob { _remainingDependencies = 1, OnExecute = _ => order.Enqueue("left") };
        var right = new TestJob { _remainingDependencies = 1, OnExecute = _ => order.Enqueue("right") };
        var join = new TestJob { _remainingDependencies = 2, OnExecute = _ => order.Enqueue("join") };

        root._dependents = [left, right];
        left._dependents = [join];
        right._dependents = [join];

        scheduler.Submit(root);
        Assert.True(scheduler.WaitForCompletion(millisecondsTimeout: 5000));

        var result = order.ToArray();
        Assert.Equal(4, result.Length);
        Assert.Equal("root", result[0]);
        Assert.Equal("join", result[3]);
        Assert.Contains("left", result);
        Assert.Contains("right", result);
    }

    // ── DAG: diamond ────────────────────────────────────────────────────────

    [Fact]
    public void Diamond_JoinExecutesOnce()
    {
        using var pool = new WorkerPool(workerCount: 4);
        var scheduler = new JobScheduler(pool);

        var joinExecutionCount = 0;

        var root = new TestJob();
        var left = new TestJob { _remainingDependencies = 1 };
        var right = new TestJob { _remainingDependencies = 1 };
        var join = new TestJob
        {
            _remainingDependencies = 2,
            OnExecute = _ => Interlocked.Increment(ref joinExecutionCount),
        };

        root._dependents = [left, right];
        left._dependents = [join];
        right._dependents = [join];

        scheduler.Submit(root);
        Assert.True(scheduler.WaitForCompletion(millisecondsTimeout: 5000));

        Assert.Equal(1, joinExecutionCount);
    }

    // ── Sub-job enqueue from within Execute ─────────────────────────────────

    [Fact]
    public void JobCanEnqueueSubJobs()
    {
        using var pool = new WorkerPool(workerCount: 2);
        var scheduler = new JobScheduler(pool);

        var subJobExecuted = false;
        var subJob = new TestJob
        {
            OnExecute = _ => subJobExecuted = true,
        };

        var parentJob = new TestJob
        {
            OnExecute = ctx => ctx.Enqueue(subJob),
        };

        scheduler.Submit(parentJob);
        Assert.True(scheduler.WaitForCompletion(millisecondsTimeout: 5000));

        Assert.True(parentJob.Executed);
        Assert.True(subJobExecuted);
    }

    // ── Outstanding counter ─────────────────────────────────────────────────

    [Fact]
    public void OutstandingCount_ReachesZeroAfterCompletion()
    {
        using var pool = new WorkerPool(workerCount: 2);
        var scheduler = new JobScheduler(pool);

        var job = new TestJob();
        scheduler.Submit(job);
        Assert.True(scheduler.WaitForCompletion(millisecondsTimeout: 5000));

        Assert.Equal(0, scheduler.GetTestAccessor().OutstandingJobs);
    }

    // ── Pool integration ────────────────────────────────────────────────────

    [Fact]
    public void PooledJobs_StateResetCorrectly()
    {
        var jobPool = new JobPool<TestJob>();

        var job = jobPool.Rent();
        job._remainingDependencies = 3;
        job._dependents = [new TestJob()];
#if DEBUG
        job._state = JobState.Completed;
#endif

        jobPool.Return(job);
        var reused = jobPool.Rent();

        Assert.Same(job, reused);
        Assert.Equal(0, reused._remainingDependencies);
        Assert.Null(reused._dependents);
        Assert.Null(reused._scheduler);
#if DEBUG
        Assert.Equal(JobState.Pending, reused._state);
#endif
    }

    // ── Concurrent stress ───────────────────────────────────────────────────

    [Fact]
    public void Stress_ManyIndependentJobs()
    {
        const int JobCount = 200;
        using var pool = new WorkerPool(workerCount: 4);
        var scheduler = new JobScheduler(pool);

        var executionCount = 0;

        for (var i = 0; i < JobCount; i++)
        {
            var job = new TestJob
            {
                OnExecute = _ => Interlocked.Increment(ref executionCount),
            };
            scheduler.Submit(job);
        }

        Assert.True(scheduler.WaitForCompletion(millisecondsTimeout: 10000));
        Assert.Equal(JobCount, executionCount);
    }

    [Fact]
    public void Stress_DeepChain()
    {
        const int Depth = 100;
        using var pool = new WorkerPool(workerCount: 4);
        var scheduler = new JobScheduler(pool);

        var order = new ConcurrentQueue<int>();

        var jobs = new TestJob[Depth];
        for (var i = 0; i < Depth; i++)
        {
            var idx = i;
            jobs[i] = new TestJob
            {
                _remainingDependencies = i == 0 ? 0 : 1,
                OnExecute = _ => order.Enqueue(idx),
            };
        }

        for (var i = 0; i < Depth - 1; i++)
            jobs[i]._dependents = [jobs[i + 1]];

        scheduler.Submit(jobs[0]);
        Assert.True(scheduler.WaitForCompletion(millisecondsTimeout: 10000));

        var result = order.ToArray();
        Assert.Equal(Depth, result.Length);
        for (var i = 0; i < Depth; i++)
            Assert.Equal(i, result[i]);
    }

    [Fact]
    public void Stress_WideFanOut()
    {
        const int FanWidth = 50;
        using var pool = new WorkerPool(workerCount: 4);
        var scheduler = new JobScheduler(pool);

        var executionCount = 0;

        var join = new TestJob
        {
            _remainingDependencies = FanWidth,
        };

        var root = new TestJob();
        var children = new Job[FanWidth];
        for (var i = 0; i < FanWidth; i++)
        {
            children[i] = new TestJob
            {
                _remainingDependencies = 1,
                OnExecute = _ => Interlocked.Increment(ref executionCount),
                _dependents = [join],
            };
        }

        root._dependents = children;
        scheduler.Submit(root);
        Assert.True(scheduler.WaitForCompletion(millisecondsTimeout: 10000));

        Assert.Equal(FanWidth, executionCount);
    }

    // ── Two schedulers sharing one pool ──────────────────────────────────────

    [Fact]
    public void TwoSchedulers_SharePool_BothComplete()
    {
        using var pool = new WorkerPool(workerCount: 4);
        var schedulerA = new JobScheduler(pool);
        var schedulerB = new JobScheduler(pool);

        var countA = 0;
        var countB = 0;

        for (var i = 0; i < 50; i++)
            schedulerA.Submit(new TestJob { OnExecute = _ => Interlocked.Increment(ref countA) });
        for (var i = 0; i < 50; i++)
            schedulerB.Submit(new TestJob { OnExecute = _ => Interlocked.Increment(ref countB) });

        Assert.True(schedulerA.WaitForCompletion(millisecondsTimeout: 10000));
        Assert.True(schedulerB.WaitForCompletion(millisecondsTimeout: 10000));

        Assert.Equal(50, countA);
        Assert.Equal(50, countB);
    }

    // ── Dispose ─────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_WorkersShutDown()
    {
        var pool = new WorkerPool(workerCount: 2);
        pool.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var pool = new WorkerPool(workerCount: 2);
        pool.Dispose();
        pool.Dispose();
    }
}
