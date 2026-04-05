using System.Diagnostics;

namespace Fabrica.Core.Jobs;

/// <summary>
/// DAG-based job scheduler backed by per-worker work-stealing deques (Chase-Lev). Manages N
/// background worker threads; the coordinator thread parks while workers execute.
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
///   for sub-jobs happen during the parent's execution (before the parent's decrement). The worker
///   that decrements to zero signals <see cref="_completionSignal"/>, unparking the coordinator.
///
///   TODO: The current single-counter/single-signal design assumes one batch at a time. To share
///   workers across multiple submitters (e.g. sim + render), completion tracking needs to move to a
///   per-DAG fence object (JobFence) with its own atomic count and signal.
///
/// WORKER LIFECYCLE
///   Uses the announce-then-recheck pattern: when a worker finds no work, it atomically increments
///   <see cref="_parkedWorkers"/>, re-checks for work (closing the missed-wake race), and only
///   then blocks on <see cref="_workSignal"/>. <see cref="NotifyWorkAvailable"/> only releases the
///   semaphore when parked workers exist, preventing unbounded permit accumulation.
///   <see cref="Dispose"/> sets a shutdown flag, wakes all workers, and joins their threads.
///
/// COORDINATOR MODEL
///   The coordinator thread (<see cref="Submit"/>/<see cref="WaitForCompletion"/>) does not execute
///   jobs. It pushes submitted jobs onto its own deque (workers steal from it), then parks on
///   <see cref="_completionSignal"/> until all work is done.
///
/// REFERENCE
///   D. Chase and Y. Lev, "Dynamic Circular Work-Stealing Deque," SPAA 2005.
/// </summary>
internal sealed class JobScheduler : IDisposable
{
    /// <summary>
    /// All worker contexts plus the coordinator's submission context. Length is
    /// <see cref="_workerCount"/> + 1. Indices 0..<see cref="_workerCount"/>-1 are background worker
    /// contexts; the last element (index <see cref="_workerCount"/>) holds the deque that
    /// <see cref="Submit"/> pushes onto. Workers steal from all contexts, including the submission
    /// deque.
    /// </summary>
    private readonly WorkerContext[] _allContexts;

    /// <summary>Background worker threads. Length equals <see cref="_workerCount"/>.</summary>
    private readonly Thread[] _workerThreads;

    /// <summary>Number of background worker threads (does not include the coordinator).</summary>
    private readonly int _workerCount;

    /// <summary>
    /// Shared semaphore for parking idle workers. Only released when <see cref="_parkedWorkers"/>
    /// is positive, preventing unbounded permit accumulation.
    /// </summary>
    private readonly SemaphoreSlim _workSignal;

    /// <summary>
    /// Signaled by the worker that decrements <see cref="_outstandingJobs"/> to zero, unparking the
    /// coordinator in <see cref="WaitForCompletion"/>. Reset at the start of each wait cycle.
    /// </summary>
    private readonly ManualResetEventSlim _completionSignal;

    /// <summary>Set to <c>true</c> during <see cref="Dispose"/> to break worker loops.</summary>
    private volatile bool _shutdownRequested;

    /// <summary>Guard for idempotent <see cref="Dispose"/>.</summary>
    private int _disposed;

    /// <summary>
    /// Number of jobs that have been enqueued but not yet completed. Incremented on every enqueue
    /// (submit, sub-job enqueue, dependency propagation); decremented after each execution
    /// completes (including dependency propagation). Reaches zero exactly when all DAG work is done.
    /// The worker that decrements to zero signals <see cref="_completionSignal"/>.
    /// </summary>
    private int _outstandingJobs;

    /// <summary>
    /// Number of workers currently parked (blocked on <see cref="_workSignal"/>). Used by
    /// <see cref="NotifyWorkAvailable"/> to avoid releasing the semaphore when no one is waiting,
    /// which would cause unbounded permit accumulation. Updated via announce-then-recheck: workers
    /// increment before re-checking for work, and decrement after waking or finding work.
    /// </summary>
    private int _parkedWorkers;

    /// <summary>
    /// Creates a scheduler with <paramref name="workerCount"/> background worker threads. If
    /// negative, defaults to <see cref="Environment.ProcessorCount"/> (clamped to
    /// <see cref="MaxWorkerCount"/>). Must be at least 1.
    /// </summary>
    internal const int MaxWorkerCount = 127;

    internal JobScheduler(int workerCount = -1)
    {
        if (workerCount < 0)
            workerCount = Math.Min(Environment.ProcessorCount, MaxWorkerCount);

        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(workerCount, MaxWorkerCount);

        _workerCount = workerCount;
        _workSignal = new SemaphoreSlim(0);
        _completionSignal = new ManualResetEventSlim(false);

        _allContexts = new WorkerContext[workerCount + 1];
        for (var i = 0; i <= workerCount; i++)
            _allContexts[i] = new WorkerContext(this, i);

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
    /// Submits a ready-to-execute job. The job is pushed onto the coordinator's deque (following the
    /// Chase-Lev owner-push model) where workers can steal it. Must be called from the coordinator
    /// thread.
    /// </summary>
    internal void Submit(Job job)
    {
#if DEBUG
        Debug.Assert(job._state == JobState.Pending);
        job._state = JobState.Queued;
#endif
        Interlocked.Increment(ref _outstandingJobs);
        _allContexts[_workerCount].Deque.Push(job);
        this.NotifyWorkAvailable();
    }

    /// <summary>
    /// Parks the coordinator thread until all outstanding jobs have completed. Workers execute all
    /// work; the coordinator does not participate. Returns <c>true</c> if completed, <c>false</c>
    /// if the timeout expired. Pass <c>-1</c> for no timeout.
    /// </summary>
    internal bool WaitForCompletion(int millisecondsTimeout = -1)
    {
        if (Volatile.Read(ref _outstandingJobs) == 0)
            return true;

        _completionSignal.Reset();

        if (Volatile.Read(ref _outstandingJobs) == 0)
            return true;

        if (millisecondsTimeout >= 0)
            return _completionSignal.Wait(millisecondsTimeout);

        _completionSignal.Wait();
        return true;
    }

    /// <summary>
    /// Signals that work is available, waking at most one parked worker. Only releases the
    /// semaphore when at least one worker is parked, preventing unbounded permit accumulation.
    /// </summary>
    internal void NotifyWorkAvailable()
    {
        if (Volatile.Read(ref _parkedWorkers) > 0)
            _workSignal.Release();
    }

    /// <summary>
    /// Atomically increments the outstanding job count. Called by <see cref="WorkerContext.Enqueue"/>
    /// when a job pushes sub-jobs during execution.
    /// </summary>
    internal void IncrementOutstanding() => Interlocked.Increment(ref _outstandingJobs);

    // ── Worker loop ─────────────────────────────────────────────────────────

    private void RunWorker(WorkerContext context)
    {
        Threading.ThreadPinningNative.TryPinCurrentThread(context.WorkerIndex);

        while (!_shutdownRequested)
        {
            if (this.TryExecuteOne(context))
                continue;

            // Announce-then-recheck: declare intent to park, then re-check for work. This closes
            // the race where work arrives between our failed steal and the Wait call — the producer
            // sees _parkedWorkers > 0 and releases, or our re-check finds the work directly.
            Interlocked.Increment(ref _parkedWorkers);

            if (this.TryExecuteOne(context))
            {
                Interlocked.Decrement(ref _parkedWorkers);
                continue;
            }

            _workSignal.Wait();
            Interlocked.Decrement(ref _parkedWorkers);
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
#if DEBUG
        job._state = JobState.Executing;
#endif

        job.Execute(context);

#if DEBUG
        job._state = JobState.Completed;
#endif

        this.PropagateCompletion(job, context);

        if (Interlocked.Decrement(ref _outstandingJobs) == 0)
            _completionSignal.Set();
    }

    private void PropagateCompletion(Job job, WorkerContext context)
    {
        if (job._dependents is not { } dependents)
            return;

        foreach (var dependent in dependents)
        {
            var remaining = Interlocked.Decrement(ref dependent._remainingDependencies);
            Debug.Assert(remaining >= 0);
            if (remaining != 0)
                continue;

#if DEBUG
            Debug.Assert(dependent._state == JobState.Pending);
            dependent._state = JobState.Queued;
#endif
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
        _workSignal.Release(_workerCount);

        foreach (var thread in _workerThreads)
            thread.Join();

        _workSignal.Dispose();
        _completionSignal.Dispose();
    }

    // ── Test accessor ───────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor(JobScheduler scheduler)
    {
        public WorkerContext SubmissionContext => scheduler._allContexts[scheduler._workerCount];
        public WorkerContext GetWorkerContext(int index) => scheduler._allContexts[index];
        public int TotalContextCount => scheduler._allContexts.Length;
        public int OutstandingJobs => Volatile.Read(ref scheduler._outstandingJobs);
    }
}
