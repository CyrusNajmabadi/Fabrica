using System.Collections.Concurrent;
using System.Diagnostics;

namespace Fabrica.Core.Jobs;

/// <summary>
/// Shared pool of worker threads backed by per-worker work-stealing deques (Chase-Lev). Multiple
/// <see cref="JobScheduler"/> instances share a single pool; top-level jobs are submitted via
/// <see cref="Inject"/>.
///
/// <para>
/// The pool consists of <c>workerCount</c> background threads and <c>coordinatorCount</c>
/// coordinator slots. Coordinators are external threads (e.g. the game loop) that participate in
/// work-stealing when they submit a job DAG. Each <see cref="JobScheduler"/> attaches to one
/// coordinator slot via <see cref="AttachCoordinator"/>.
/// </para>
///
/// REFERENCE: D. Chase and Y. Lev, "Dynamic Circular Work-Stealing Deque," SPAA 2005.
/// </summary>
public sealed class WorkerPool : IDisposable
{
    /// <summary>
    /// All worker contexts: background workers at indices <c>[0, _backgroundWorkerCount)</c>,
    /// coordinator slots at <c>[_backgroundWorkerCount, _backgroundWorkerCount + coordinatorCount)</c>.
    /// </summary>
    private readonly WorkerContext[] _allContexts;

    /// <summary>Background worker threads. Length equals <see cref="_backgroundWorkerCount"/>.</summary>
    private readonly Thread[] _workerThreads;

    /// <summary>Number of background worker threads (excludes coordinator slots).</summary>
    private readonly int _backgroundWorkerCount;

    /// <summary>Total number of worker contexts (background + coordinator).</summary>
    private readonly int _totalWorkerCount;

    /// <summary>
    /// Shared semaphore for parking idle background workers. Only released when
    /// <see cref="_parkedWorkers"/> is positive, preventing unbounded permit accumulation.
    /// </summary>
    private readonly SemaphoreSlim _workSignal;

    /// <summary>Set to <c>true</c> during <see cref="Dispose"/> to break worker loops.</summary>
    private volatile bool _shutdownRequested;

    /// <summary>Guard for idempotent <see cref="Dispose"/>.</summary>
    private int _disposed;

    /// <summary>
    /// Number of background workers currently parked (blocked on <see cref="_workSignal"/>). Used
    /// by <see cref="NotifyWorkAvailable"/> to avoid releasing the semaphore when no one is
    /// waiting, which would cause unbounded permit accumulation. Updated via announce-then-recheck.
    /// </summary>
    private int _parkedWorkers;

    /// <summary>
    /// Top-level jobs injected via the test accessor's <see cref="JobScheduler.TestAccessor.Inject"/>
    /// land here. Not used by the coordinator fast-path (<see cref="JobScheduler.Submit"/>), which
    /// pushes directly onto the coordinator's deque.
    /// </summary>
    private readonly ConcurrentQueue<Job> _injectionQueue = new();

    /// <summary>Next coordinator slot index to hand out via <see cref="AttachCoordinator"/>.</summary>
    private int _nextCoordinatorSlot;

    internal const int MaxWorkerCount = 127;

    public WorkerPool(int workerCount = -1, int coordinatorCount = 0)
    {
        if (workerCount < 0)
            workerCount = Math.Min(Environment.ProcessorCount, MaxWorkerCount);

        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(coordinatorCount, 0);

        var totalCount = workerCount + coordinatorCount;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalCount, MaxWorkerCount);

        _backgroundWorkerCount = workerCount;
        _totalWorkerCount = totalCount;
        _workSignal = new SemaphoreSlim(0);

        _allContexts = new WorkerContext[totalCount];
        for (var i = 0; i < totalCount; i++)
            _allContexts[i] = new WorkerContext(this, i);

        _workerThreads = new Thread[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            var ctx = _allContexts[i];
            _workerThreads[i] = new Thread(() => this.RunWorker(ctx))
            {
                IsBackground = true,
                Name = $"WorkerPool-Worker-{i}",
            };
            _workerThreads[i].Start();
        }
    }

    /// <summary>
    /// Total number of worker contexts (background threads + coordinator slots). Use this to size
    /// per-worker data structures (e.g. thread-local buffers).
    /// </summary>
    public int WorkerCount => _totalWorkerCount;

    /// <summary>
    /// Claims the next available coordinator slot and returns its <see cref="WorkerContext"/>.
    /// Each <see cref="JobScheduler"/> calls this once during construction.
    /// </summary>
    internal WorkerContext AttachCoordinator()
    {
        var slot = Interlocked.Increment(ref _nextCoordinatorSlot) - 1;
        var index = _backgroundWorkerCount + slot;
        Debug.Assert(index < _allContexts.Length, $"No coordinator slots remaining (requested slot {slot}, total contexts {_allContexts.Length}).");
        return _allContexts[index];
    }

    // ── Job injection ─────────────────────────────────────────────────────────

    /// <summary>
    /// Enqueues a job into the shared injection queue and wakes a parked worker.
    /// </summary>
    internal void Inject(Job job)
    {
        // Called by JobScheduler.Submit after stamping the job with its scheduler.
        _injectionQueue.Enqueue(job);
        this.NotifyWorkAvailable();
    }

    /// <summary>
    /// Signals that work is available, waking at most one parked worker.
    /// </summary>
    internal void NotifyWorkAvailable()
    {
        // Only release when someone is parked so permits do not accumulate without bound.
        if (Volatile.Read(ref _parkedWorkers) > 0)
            _workSignal.Release();
    }

    // ── Worker loop ─────────────────────────────────────────────────────────

    private void RunWorker(WorkerContext context)
    {
        // WORKER LIFECYCLE: announce-then-recheck — when a worker finds no work, it increments _parkedWorkers, re-checks
        // for work (closing the missed-wake race), then blocks on _workSignal. NotifyWorkAvailable coordinates with
        // _parkedWorkers (see that method).
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

    /// <summary>
    /// Attempts to find and execute one job. Tries local deque, then steals from peers, then
    /// checks the injection queue. Called by both background workers and coordinator threads.
    /// </summary>
    internal bool TryExecuteOne(WorkerContext context)
    {
        // WORK DISCOVERY PRIORITY: (1) pop own deque — LIFO, cache-hot; (2) steal from peers — FIFO; (3) shared injection
        // queue — cold, after local and peer deques (JobScheduler.Submit enqueues via Inject).
        if (context.Deque.TryPop(out var job))
        {
            this.ExecuteJob(job, context);
            return true;
        }

        if (this.TryStealAndExecute(context))
            return true;

        return this.TryDequeueInjected(context);
    }

    private bool TryStealAndExecute(WorkerContext context)
    {
        var count = _allContexts.Length;
        var start = ++context.StealOffset;

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

    private bool TryDequeueInjected(WorkerContext context)
    {
        // Dequeue top-level jobs after exhausting local deque and peer steals (see TryExecuteOne).
        if (_injectionQueue.TryDequeue(out var job))
        {
            this.ExecuteJob(job, context);
            return true;
        }

        return false;
    }

    private void ExecuteJob(Job job, WorkerContext context)
    {
        var scheduler = job.Scheduler!;
        context.CurrentScheduler = scheduler;

#if DEBUG
        job.State = JobState.Executing;
#endif

        job.Execute(new JobContext(context));

#if DEBUG
        job.State = JobState.Completed;
#endif

        // SCHEDULING MODEL: after Execute, decrement dependents' RemainingDependencies; any that reach zero are pushed on
        // this worker's deque.
        this.PropagateCompletion(job, context);
        scheduler.DecrementOutstanding();

        context.CurrentScheduler = null;
    }

    private void PropagateCompletion(Job job, WorkerContext context)
    {
        if (job.Dependents is not { Count: > 0 } dependents)
            return;

        var scheduler = context.CurrentScheduler!;

        for (var i = 0; i < dependents.Count; i++)
        {
            var dependent = dependents[i];
            var remaining = Interlocked.Decrement(ref dependent.RemainingDependencies);
            Debug.Assert(remaining >= 0);
            if (remaining != 0)
                continue;

#if DEBUG
            Debug.Assert(dependent.State == JobState.Pending);
            dependent.State = JobState.Queued;
#endif
            dependent.Scheduler = scheduler;
            scheduler.IncrementOutstanding();
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
        _workSignal.Release(_backgroundWorkerCount);

        foreach (var thread in _workerThreads)
            thread.Join();

        _workSignal.Dispose();
    }

    // ── Test accessor ───────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor(WorkerPool pool)
    {
        public WorkerContext GetWorkerContext(int index) => pool._allContexts[index];
        public int BackgroundWorkerCount => pool._backgroundWorkerCount;
        public int WorkerCount => pool._totalWorkerCount;
    }
}
