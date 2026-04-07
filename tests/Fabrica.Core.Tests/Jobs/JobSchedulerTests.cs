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
        public Action<JobContext>? OnExecute { get; set; }

        protected internal override void Execute(JobContext context)
        {
            this.ExecutedOnWorker = context.WorkerIndex;
            this.Executed = true;
            this.OnExecute?.Invoke(context);
        }

        protected override void ResetState()
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
        using var pool = new WorkerPool(workerCount: 2, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        var job = new TestJob();
        accessor.Submit(job);

        Assert.True(job.Executed);
    }

    [Fact]
    public void SingleJob_ReceivesWorkerContext()
    {
        using var pool = new WorkerPool(workerCount: 2, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        var capturedIndex = -1;
        var job = new TestJob
        {
            OnExecute = ctx => capturedIndex = ctx.WorkerIndex,
        };

        accessor.Submit(job);

        Assert.True(capturedIndex >= 0);
    }

    // ── DAG: chain ──────────────────────────────────────────────────────────

    [Fact]
    public void Chain_ExecutesInDependencyOrder()
    {
        using var pool = new WorkerPool(workerCount: 2, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        var order = new ConcurrentQueue<string>();

        var jobA = new TestJob { OnExecute = _ => order.Enqueue("A") };
        var jobB = new TestJob { OnExecute = _ => order.Enqueue("B") };
        var jobC = new TestJob { OnExecute = _ => order.Enqueue("C") };

        jobB.DependsOn(jobA);
        jobC.DependsOn(jobB);

        accessor.Submit(jobA);

        Assert.Equal(["A", "B", "C"], [.. order]);
    }

    // ── DAG: fan-out / fan-in ───────────────────────────────────────────────

    [Fact]
    public void FanOutFanIn_RespectsDependencies()
    {
        using var pool = new WorkerPool(workerCount: 4, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        var order = new ConcurrentQueue<string>();

        var root = new TestJob { OnExecute = _ => order.Enqueue("root") };
        var left = new TestJob { OnExecute = _ => order.Enqueue("left") };
        var right = new TestJob { OnExecute = _ => order.Enqueue("right") };
        var join = new TestJob { OnExecute = _ => order.Enqueue("join") };

        left.DependsOn(root);
        right.DependsOn(root);
        join.DependsOn(left);
        join.DependsOn(right);

        accessor.Submit(root);

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
        using var pool = new WorkerPool(workerCount: 4, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        var joinExecutionCount = 0;

        var root = new TestJob();
        var left = new TestJob();
        var right = new TestJob();
        var join = new TestJob
        {
            OnExecute = _ => Interlocked.Increment(ref joinExecutionCount),
        };

        left.DependsOn(root);
        right.DependsOn(root);
        join.DependsOn(left);
        join.DependsOn(right);

        accessor.Submit(root);

        Assert.Equal(1, joinExecutionCount);
    }

    // ── Sub-job enqueue from within Execute ─────────────────────────────────

    [Fact]
    public void JobCanEnqueueSubJobs()
    {
        using var pool = new WorkerPool(workerCount: 2, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        var subJobExecuted = false;
        var subJob = new TestJob
        {
            OnExecute = _ => subJobExecuted = true,
        };

        var parentJob = new TestJob
        {
            OnExecute = ctx => ctx.WorkerContext.Enqueue(subJob),
        };

        accessor.Submit(parentJob);

        Assert.True(parentJob.Executed);
        Assert.True(subJobExecuted);
    }

    // ── Outstanding counter ─────────────────────────────────────────────────

    [Fact]
    public void OutstandingCount_ReachesZeroAfterCompletion()
    {
        using var pool = new WorkerPool(workerCount: 2, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        var job = new TestJob();
        accessor.Submit(job);

        Assert.Equal(0, accessor.OutstandingJobs);
    }

    // ── Pool integration ────────────────────────────────────────────────────

    [Fact]
    public void PooledJobs_StateResetCorrectly()
    {
        var jobPool = new JobPool<TestJob>();

        var job = jobPool.Rent();
        new TestJob().DependsOn(job);
        new TestJob().DependsOn(job);
#if DEBUG
        job.State = JobState.Completed;
#endif

        jobPool.Return(job);
        var reused = jobPool.Rent();

        Assert.Same(job, reused);
        Assert.Equal(0, reused.RemainingDependencies);
        Assert.Equal(0, reused.Dependents!.Count);
        Assert.Null(reused.Scheduler);
#if DEBUG
        Assert.Equal(JobState.Pending, reused.State);
#endif
    }

    // ── Concurrent stress ───────────────────────────────────────────────────

    [Fact]
    public void Stress_ManyIndependentJobs()
    {
        const int JobCount = 200;
        using var pool = new WorkerPool(workerCount: 4, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        var executionCount = 0;

        for (var i = 0; i < JobCount; i++)
        {
            var job = new TestJob
            {
                OnExecute = _ => Interlocked.Increment(ref executionCount),
            };
            accessor.Inject(job);
        }

        accessor.WaitForCompletion();
        Assert.Equal(JobCount, executionCount);
    }

    [Fact]
    public void Stress_DeepChain()
    {
        const int Depth = 100;
        using var pool = new WorkerPool(workerCount: 4, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        var order = new ConcurrentQueue<int>();

        var jobs = new TestJob[Depth];
        for (var i = 0; i < Depth; i++)
        {
            var capturedJobIndex = i;
            jobs[i] = new TestJob
            {
                OnExecute = _ => order.Enqueue(capturedJobIndex),
            };
        }

        for (var i = 1; i < Depth; i++)
            jobs[i].DependsOn(jobs[i - 1]);

        accessor.Submit(jobs[0]);

        var result = order.ToArray();
        Assert.Equal(Depth, result.Length);
        for (var i = 0; i < Depth; i++)
            Assert.Equal(i, result[i]);
    }

    [Fact]
    public void Stress_WideFanOut()
    {
        const int FanWidth = 50;
        using var pool = new WorkerPool(workerCount: 4, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        var executionCount = 0;

        var join = new TestJob();

        var root = new TestJob();
        for (var i = 0; i < FanWidth; i++)
        {
            var child = new TestJob
            {
                OnExecute = _ => Interlocked.Increment(ref executionCount),
            };
            child.DependsOn(root);
            join.DependsOn(child);
        }
        accessor.Submit(root);

        Assert.Equal(FanWidth, executionCount);
    }

    // ── Two schedulers sharing one pool ──────────────────────────────────────

    [Fact]
    public void TwoSchedulers_SharePool_BothComplete()
    {
        using var pool = new WorkerPool(workerCount: 4, coordinatorCount: 2);
        var schedulerA = new JobScheduler(pool);
        var schedulerB = new JobScheduler(pool);
        var accessorA = schedulerA.GetTestAccessor();
        var accessorB = schedulerB.GetTestAccessor();

        var countA = 0;
        var countB = 0;

        for (var i = 0; i < 50; i++)
            accessorA.Inject(new TestJob { OnExecute = _ => Interlocked.Increment(ref countA) });
        for (var i = 0; i < 50; i++)
            accessorB.Inject(new TestJob { OnExecute = _ => Interlocked.Increment(ref countB) });

        accessorA.WaitForCompletion();
        accessorB.WaitForCompletion();

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
