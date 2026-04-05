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
        public Action<TestJob>? OnExecute { get; set; }

        internal override void Execute()
        {
            this.ExecutedOnWorker = _workerContext!.WorkerIndex;
            this.Executed = true;
            this.OnExecute?.Invoke(this);
        }

        internal override void Reset()
        {
            this.Executed = false;
            this.ExecutedOnWorker = -1;
            this.OnExecute = null;
        }
    }

    private sealed class NopJob : Job
    {
        internal override void Execute() { }
        internal override void Reset() { }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Creates a NopJob whose counter starts at <paramref name="count"/>, used as a
    /// terminal signal for <see cref="JobScheduler.WaitForCompletion"/>.</summary>
    private static NopJob CreateSignal(int count = 1) => new() { _counter = new JobCounter(count) };

    // ── Basic execution ─────────────────────────────────────────────────────

    [Fact]
    public void SingleJob_Executes()
    {
        using var scheduler = new JobScheduler(workerCount: 2);

        var signal = CreateSignal();
        var job = new TestJob { _dependents = [signal] };

        scheduler.Submit(job);
        Assert.True(scheduler.WaitForCompletion(signal, millisecondsTimeout: 5000));

        Assert.True(job.Executed);
    }

    [Fact]
    public void SingleJob_ReceivesWorkerContext()
    {
        using var scheduler = new JobScheduler(workerCount: 2);

        var signal = CreateSignal();
        var capturedIndex = -1;

        var job = new TestJob
        {
            _dependents = [signal],
            OnExecute = j => capturedIndex = j.ExecutedOnWorker,
        };

        scheduler.Submit(job);
        Assert.True(scheduler.WaitForCompletion(signal, millisecondsTimeout: 5000));

        Assert.True(capturedIndex >= 0);
    }

    // ── DAG: chain ──────────────────────────────────────────────────────────

    [Fact]
    public void Chain_ExecutesInDependencyOrder()
    {
        using var scheduler = new JobScheduler(workerCount: 2);

        var signal = CreateSignal();
        var order = new ConcurrentQueue<string>();

        var jobA = new TestJob { OnExecute = _ => order.Enqueue("A") };
        var jobB = new TestJob { _counter = new JobCounter(1), OnExecute = _ => order.Enqueue("B") };
        var jobC = new TestJob { _counter = new JobCounter(1), OnExecute = _ => order.Enqueue("C") };

        jobA._dependents = [jobB];
        jobB._dependents = [jobC];
        jobC._dependents = [signal];

        scheduler.Submit(jobA);
        Assert.True(scheduler.WaitForCompletion(signal, millisecondsTimeout: 5000));

        var result = order.ToArray();
        Assert.Equal(["A", "B", "C"], result);
    }

    // ── DAG: fan-out / fan-in ───────────────────────────────────────────────

    [Fact]
    public void FanOutFanIn_RespectsDependencies()
    {
        using var scheduler = new JobScheduler(workerCount: 4);

        var signal = CreateSignal();
        var order = new ConcurrentQueue<string>();

        var root = new TestJob { OnExecute = _ => order.Enqueue("root") };
        var left = new TestJob { _counter = new JobCounter(1), OnExecute = _ => order.Enqueue("left") };
        var right = new TestJob { _counter = new JobCounter(1), OnExecute = _ => order.Enqueue("right") };
        var join = new TestJob { _counter = new JobCounter(2), OnExecute = _ => order.Enqueue("join") };

        root._dependents = [left, right];
        left._dependents = [join];
        right._dependents = [join];
        join._dependents = [signal];

        scheduler.Submit(root);
        Assert.True(scheduler.WaitForCompletion(signal, millisecondsTimeout: 5000));

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
        using var scheduler = new JobScheduler(workerCount: 4);

        var signal = CreateSignal();
        var joinExecutionCount = 0;

        var root = new TestJob();
        var left = new TestJob { _counter = new JobCounter(1) };
        var right = new TestJob { _counter = new JobCounter(1) };
        var join = new TestJob
        {
            _counter = new JobCounter(2),
            OnExecute = _ => Interlocked.Increment(ref joinExecutionCount),
        };

        root._dependents = [left, right];
        left._dependents = [join];
        right._dependents = [join];
        join._dependents = [signal];

        scheduler.Submit(root);
        Assert.True(scheduler.WaitForCompletion(signal, millisecondsTimeout: 5000));

        Assert.Equal(1, joinExecutionCount);
    }

    // ── Terminal counter holder is not re-executed ───────────────────────────

    [Fact]
    public void TerminalCounterHolder_NotReExecuted()
    {
        using var scheduler = new JobScheduler(workerCount: 2);

        var executionCount = 0;
        var terminal = new TestJob
        {
            _counter = new JobCounter(1),
            OnExecute = _ => Interlocked.Increment(ref executionCount),
        };

        var child = new TestJob
        {
            _counter = new JobCounter(1),
            _dependents = [terminal]
        };
        terminal._dependents = [child];

        scheduler.Submit(terminal);
        Assert.True(scheduler.WaitForCompletion(terminal, millisecondsTimeout: 5000));

        Assert.Equal(1, executionCount);
    }

    // ── Coordinator-only mode (zero workers) ────────────────────────────────

    [Fact]
    public void ZeroWorkers_CoordinatorExecutesEverything()
    {
        using var scheduler = new JobScheduler(workerCount: 0);

        var signal = CreateSignal();
        var order = new ConcurrentQueue<string>();

        var jobA = new TestJob { OnExecute = _ => order.Enqueue("A") };
        var jobB = new TestJob { _counter = new JobCounter(1), OnExecute = _ => order.Enqueue("B") };

        jobA._dependents = [jobB];
        jobB._dependents = [signal];

        scheduler.Submit(jobA);
        Assert.True(scheduler.WaitForCompletion(signal, millisecondsTimeout: 5000));

        Assert.Equal(["A", "B"], [.. order]);
    }

    // ── Sub-job enqueue from within Execute ─────────────────────────────────

    [Fact]
    public void JobCanEnqueueSubJobs()
    {
        using var scheduler = new JobScheduler(workerCount: 2);

        var signal = CreateSignal();
        var subJobExecuted = false;

        var subJob = new TestJob
        {
            OnExecute = _ => subJobExecuted = true,
            _dependents = [signal],
        };

        var parentJob = new TestJob
        {
            OnExecute = j => j._workerContext!.Enqueue(subJob),
        };

        scheduler.Submit(parentJob);
        Assert.True(scheduler.WaitForCompletion(signal, millisecondsTimeout: 5000));

        Assert.True(parentJob.Executed);
        Assert.True(subJobExecuted);
    }

    // ── Pool integration ────────────────────────────────────────────────────

    [Fact]
    public void PooledJobs_StateResetCorrectly()
    {
        var pool = new JobPool<TestJob>();

        var job = pool.Rent();
        job._counter = new JobCounter(3);
        job._dependents = [new NopJob()];
        job._state = JobState.Completed;
        job._workerContext = new WorkerContext(null!, 99);

        pool.Return(job);
        var reused = pool.Rent();

        Assert.Same(job, reused);
        Assert.True(reused._counter.IsComplete);
        Assert.Null(reused._dependents);
        Assert.Equal(JobState.Pending, reused._state);
        Assert.Null(reused._workerContext);
    }

    // ── Concurrent stress ───────────────────────────────────────────────────

    [Fact]
    public void Stress_ManyIndependentJobs()
    {
        const int JobCount = 200;
        using var scheduler = new JobScheduler(workerCount: 4);

        var signal = CreateSignal(count: JobCount);
        var executionCount = 0;

        for (var i = 0; i < JobCount; i++)
        {
            var job = new TestJob
            {
                OnExecute = _ => Interlocked.Increment(ref executionCount),
                _dependents = [signal],
            };
            scheduler.Submit(job);
        }

        Assert.True(scheduler.WaitForCompletion(signal, millisecondsTimeout: 10000));
        Assert.Equal(JobCount, executionCount);
    }

    [Fact]
    public void Stress_DeepChain()
    {
        const int Depth = 100;
        using var scheduler = new JobScheduler(workerCount: 4);

        var signal = CreateSignal();
        var order = new ConcurrentQueue<int>();

        var jobs = new TestJob[Depth];
        for (var i = 0; i < Depth; i++)
        {
            var idx = i;
            jobs[i] = new TestJob
            {
                _counter = i == 0 ? default : new JobCounter(1),
                OnExecute = _ => order.Enqueue(idx),
            };
        }

        for (var i = 0; i < Depth - 1; i++)
            jobs[i]._dependents = [jobs[i + 1]];

        jobs[Depth - 1]._dependents = [signal];

        scheduler.Submit(jobs[0]);
        Assert.True(scheduler.WaitForCompletion(signal, millisecondsTimeout: 10000));

        var result = order.ToArray();
        Assert.Equal(Depth, result.Length);
        for (var i = 0; i < Depth; i++)
            Assert.Equal(i, result[i]);
    }

    [Fact]
    public void Stress_WideFanOut()
    {
        const int FanWidth = 50;
        using var scheduler = new JobScheduler(workerCount: 4);

        var signal = CreateSignal();
        var executionCount = 0;

        var join = new TestJob
        {
            _counter = new JobCounter(FanWidth),
            _dependents = [signal],
        };

        var root = new TestJob();
        var children = new Job[FanWidth];
        for (var i = 0; i < FanWidth; i++)
        {
            children[i] = new TestJob
            {
                _counter = new JobCounter(1),
                OnExecute = _ => Interlocked.Increment(ref executionCount),
                _dependents = [join],
            };
        }

        root._dependents = children;
        scheduler.Submit(root);
        Assert.True(scheduler.WaitForCompletion(signal, millisecondsTimeout: 10000));

        Assert.Equal(FanWidth, executionCount);
    }

    // ── Dispose ─────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_WorkersShutDown()
    {
        var scheduler = new JobScheduler(workerCount: 2);
        scheduler.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var scheduler = new JobScheduler(workerCount: 2);
        scheduler.Dispose();
        scheduler.Dispose();
    }
}
