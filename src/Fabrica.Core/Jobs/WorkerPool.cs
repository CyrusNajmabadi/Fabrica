using System.Collections.Concurrent;
using System.Diagnostics;

namespace Fabrica.Core.Jobs;

/// <summary>
/// Shared pool of worker threads backed by per-worker work-stealing deques (Chase-Lev). Multiple
/// <see cref="JobScheduler"/> instances share a single pool; top-level jobs are submitted via
/// <see cref="Inject"/>.
///
/// REFERENCE: D. Chase and Y. Lev, "Dynamic Circular Work-Stealing Deque," SPAA 2005.
/// </summary>
public sealed class WorkerPool : IDisposable
{
    /// <summary>Worker contexts, one per background thread. Length equals <see cref="_workerCount"/>.</summary>
    private readonly WorkerContext[] _allContexts;

    /// <summary>Background worker threads. Length equals <see cref="_workerCount"/>.</summary>
    private readonly Thread[] _workerThreads;

    /// <summary>Number of background worker threads.</summary>
    private readonly int _workerCount;

    /// <summary>
    /// Shared semaphore for parking idle workers. Only released when <see cref="_parkedWorkers"/>
    /// is positive, preventing unbounded permit accumulation.
    /// </summary>
    private readonly SemaphoreSlim _workSignal;

    /// <summary>Set to <c>true</c> during <see cref="Dispose"/> to break worker loops.</summary>
    private volatile bool _shutdownRequested;

    /// <summary>Guard for idempotent <see cref="Dispose"/>.</summary>
    private int _disposed;

    /// <summary>
    /// Number of workers currently parked (blocked on <see cref="_workSignal"/>). Used by
    /// <see cref="NotifyWorkAvailable"/> to avoid releasing the semaphore when no one is waiting,
    /// which would cause unbounded permit accumulation. Updated via announce-then-recheck.
    /// </summary>
    private int _parkedWorkers;

    /// <summary>
    /// Top-level jobs land here.
    /// </summary>
    private readonly ConcurrentQueue<Job> _injectionQueue = new();

    internal const int MaxWorkerCount = 127;

    public WorkerPool(int workerCount = -1)
    {
        if (workerCount < 0)
            workerCount = Math.Min(Environment.ProcessorCount, MaxWorkerCount);

        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(workerCount, MaxWorkerCount);

        _workerCount = workerCount;
        _workSignal = new SemaphoreSlim(0);

        _allContexts = new WorkerContext[workerCount];
        for (var i = 0; i < workerCount; i++)
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

    public int WorkerCount => _workerCount;

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

    private bool TryExecuteOne(WorkerContext context)
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
        // WORKER LIFECYCLE: set shutdown, wake all parked workers (one release per worker), join threads, dispose sync
        // primitive.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdownRequested = true;
        _workSignal.Release(_workerCount);

        foreach (var thread in _workerThreads)
            thread.Join();

        _workSignal.Dispose();
    }

    // ── Test accessor ───────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor(WorkerPool pool)
    {
        public WorkerContext GetWorkerContext(int index) => pool._allContexts[index];
        public int WorkerCount => pool._workerCount;
    }
}
