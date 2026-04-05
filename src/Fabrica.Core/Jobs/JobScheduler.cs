using System.Diagnostics;

namespace Fabrica.Core.Jobs;

/// <summary>
/// DAG-based job scheduler backed by per-worker work-stealing deques (Chase-Lev). Manages N
/// background worker threads plus a coordinator context for the submitting thread.
///
/// SCHEDULING MODEL
///   Each worker owns a <see cref="Collections.WorkStealingDeque{T}"/>. Workers pop from their
///   own deque (LIFO — cache-hot) and steal from peers (FIFO — large tasks) when idle. After
///   executing a job, the scheduler atomically decrements all dependent jobs' remaining dependency
///   counts; any dependent whose count reaches zero is pushed onto the executing worker's deque.
///
/// COMPLETION DETECTION
///   The scheduler maintains an atomic outstanding job counter. Every enqueue (submit, sub-job
///   enqueue, or dependency propagation) increments it; every completed execution decrements it.
///   The counter reaches zero exactly when all jobs in the DAG have finished, because increments
///   for sub-jobs happen during the parent's execution (before the parent's decrement). The
///   coordinator polls this counter in <see cref="WaitForCompletion"/> while actively stealing
///   and executing work.
///
/// WORKER LIFECYCLE
///   Workers park on a shared <see cref="SemaphoreSlim"/> when no work is found (after a brief
///   spin). Each <see cref="NotifyWorkAvailable"/> call wakes at most one parked worker.
///   <see cref="Dispose"/> sets a shutdown flag, wakes all workers, and joins their threads.
///
/// REFERENCE
///   D. Chase and Y. Lev, "Dynamic Circular Work-Stealing Deque," SPAA 2005.
/// </summary>
internal sealed class JobScheduler : IDisposable
{
    private readonly WorkerContext[] _allContexts;
    private readonly WorkerContext _coordinatorContext;
    private readonly Thread[] _workerThreads;
    private readonly int _workerCount;
    private readonly SemaphoreSlim _workSignal;
    private volatile bool _shutdownRequested;
    private int _disposed;

    /// <summary>
    /// Number of jobs that have been enqueued but not yet completed. Incremented on every enqueue
    /// (submit, sub-job enqueue, dependency propagation); decremented after each execution
    /// completes (including dependency propagation). Reaches zero exactly when all DAG work is done.
    /// </summary>
    private int _outstandingJobs;

    /// <summary>
    /// Creates a scheduler with <paramref name="workerCount"/> background worker threads. If
    /// negative, defaults to <see cref="Environment.ProcessorCount"/>. Zero is valid (the
    /// coordinator does all work during <see cref="WaitForCompletion"/>).
    /// </summary>
    internal JobScheduler(int workerCount = -1)
    {
        if (workerCount < 0)
            workerCount = Environment.ProcessorCount;

        _workerCount = workerCount;
        _workSignal = new SemaphoreSlim(0);

        _allContexts = new WorkerContext[workerCount + 1];
        for (var i = 0; i <= workerCount; i++)
            _allContexts[i] = new WorkerContext(this, i);

        _coordinatorContext = _allContexts[workerCount];

        _workerThreads = new Thread[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            var ctx = _allContexts[i];
            _workerThreads[i] = new Thread(() => this.RunWorker(ctx))
            {
                IsBackground = true,
                Name = $"JobScheduler-Worker-{i}",
            };
            _workerThreads[i].Start();
        }
    }

    internal int WorkerCount => _workerCount;

    // ── Coordinator API ─────────────────────────────────────────────────────

    /// <summary>
    /// Submits a ready-to-execute job onto the coordinator's deque. Workers will steal it, or the
    /// coordinator will pop it during <see cref="WaitForCompletion"/>. Must be called from the
    /// coordinator thread.
    /// </summary>
    internal void Submit(Job job)
    {
        Debug.Assert(job._state == JobState.Pending);
        job._state = JobState.Queued;
        Interlocked.Increment(ref _outstandingJobs);
        _coordinatorContext.Deque.Push(job);
        this.NotifyWorkAvailable();
    }

    /// <summary>
    /// Blocks the coordinator thread until all outstanding jobs have completed, while actively
    /// stealing and executing work. Returns <c>true</c> if completed, <c>false</c> if the
    /// timeout expired. Pass <c>-1</c> for no timeout.
    /// </summary>
    internal bool WaitForCompletion(int millisecondsTimeout = -1)
    {
        var deadline = millisecondsTimeout >= 0
            ? Stopwatch.GetTimestamp() + (long)(millisecondsTimeout * (double)Stopwatch.Frequency / 1000)
            : long.MaxValue;

        var spinner = new SpinWait();
        while (Volatile.Read(ref _outstandingJobs) != 0)
        {
            if (millisecondsTimeout >= 0 && Stopwatch.GetTimestamp() >= deadline)
                return false;

            if (this.TryExecuteOne(_coordinatorContext))
            {
                spinner.Reset();
                continue;
            }

            spinner.SpinOnce();
        }

        return true;
    }

    /// <summary>
    /// Signals that work is available, waking at most one parked worker. Called by
    /// <see cref="Submit"/>, <see cref="WorkerContext.Enqueue"/>, and dependency propagation.
    /// </summary>
    internal void NotifyWorkAvailable() => _workSignal.Release();

    /// <summary>
    /// Atomically increments the outstanding job count. Called by <see cref="WorkerContext.Enqueue"/>
    /// when a job pushes sub-jobs during execution.
    /// </summary>
    internal void IncrementOutstanding() => Interlocked.Increment(ref _outstandingJobs);

    // ── Worker loop ─────────────────────────────────────────────────────────

    private void RunWorker(WorkerContext context)
    {
        while (!_shutdownRequested)
        {
            if (this.TryExecuteOne(context))
                continue;

            _workSignal.Wait(millisecondsTimeout: 100);
        }
    }

    // ── Core execution ──────────────────────────────────────────────────────

    private bool TryExecuteOne(WorkerContext context)
    {
        if (context.Deque.TryPop(out var job))
        {
            this.ExecuteJob(job, context);
            return true;
        }

        return this.TryStealAndExecute(context);
    }

    private bool TryStealAndExecute(WorkerContext context)
    {
        var count = _allContexts.Length;
        var start = ++context._stealOffset;

        for (var i = 0; i < count; i++)
        {
            var target = _allContexts[(int)((uint)(start + i) % (uint)count)];
            if (target.WorkerIndex == context.WorkerIndex)
                continue;

            if (target.Deque.TrySteal(out var job))
            {
                this.ExecuteJob(job, context);
                return true;
            }
        }

        return false;
    }

    private void ExecuteJob(Job job, WorkerContext context)
    {
        job._workerContext = context;
        job._state = JobState.Executing;

        job.Execute();

        job._state = JobState.Completed;
        job._workerContext = null;

        this.PropagateCompletion(job, context);
        Interlocked.Decrement(ref _outstandingJobs);
    }

    private void PropagateCompletion(Job job, WorkerContext context)
    {
        if (job._dependents is not { } dependents)
            return;

        foreach (var dependent in dependents)
        {
            if (Interlocked.Decrement(ref dependent._remainingDependencies) != 0)
                continue;

            Debug.Assert(dependent._state == JobState.Pending);
            dependent._state = JobState.Queued;
            Interlocked.Increment(ref _outstandingJobs);
            context.Deque.Push(dependent);
            this.NotifyWorkAvailable();
        }
    }

    // ── Disposal ────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdownRequested = true;

        if (_workerCount > 0)
            _workSignal.Release(_workerCount);

        foreach (var thread in _workerThreads)
            thread.Join();

        _workSignal.Dispose();
    }

    // ── Test accessor ───────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor(JobScheduler scheduler)
    {
        public WorkerContext CoordinatorContext => scheduler._coordinatorContext;
        public WorkerContext GetWorkerContext(int index) => scheduler._allContexts[index];
        public int TotalContextCount => scheduler._allContexts.Length;
        public int OutstandingJobs => Volatile.Read(ref scheduler._outstandingJobs);
    }
}
