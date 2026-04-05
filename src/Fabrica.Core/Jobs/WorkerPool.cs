using System.Collections.Concurrent;
using System.Diagnostics;

namespace Fabrica.Core.Jobs;

/// <summary>
/// Shared pool of worker threads backed by per-worker work-stealing deques (Chase-Lev). Multiple
/// <see cref="JobScheduler"/> instances register their injection queues with a single pool, allowing
/// independent DAGs to share the same workers.
///
/// SCHEDULING MODEL
///   Each worker owns a <see cref="Collections.WorkStealingDeque{T}"/>. Workers pop from their
///   own deque (LIFO — cache-hot) and steal from peers (FIFO — large tasks) when idle. After
///   executing a job, the pool atomically decrements all dependent jobs' remaining dependency
///   counts; any dependent whose count reaches zero is pushed onto the executing worker's deque.
///
/// WORK DISCOVERY PRIORITY
///   1. Pop own deque (LIFO — recently pushed sub-jobs, cache-hot).
///   2. Steal from peer deques (FIFO — redistribute work).
///   3. Dequeue from registered injection queues (cold — newly submitted top-level jobs).
///
/// WORKER LIFECYCLE
///   Uses the announce-then-recheck pattern: when a worker finds no work, it atomically increments
///   <see cref="_parkedWorkers"/>, re-checks for work (closing the missed-wake race), and only
///   then blocks on <see cref="_workSignal"/>. <see cref="NotifyWorkAvailable"/> only releases the
///   semaphore when parked workers exist, preventing unbounded permit accumulation.
///   <see cref="Dispose"/> sets a shutdown flag, wakes all workers, and joins their threads.
///
/// REFERENCE
///   D. Chase and Y. Lev, "Dynamic Circular Work-Stealing Deque," SPAA 2005.
/// </summary>
internal sealed class WorkerPool : IDisposable
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
    /// Registered injection queues from <see cref="JobScheduler"/> instances. Copy-on-write: each
    /// <see cref="RegisterInjectionQueue"/> call swaps in a new array. Workers read via
    /// <see cref="Volatile.Read{T}(ref T)"/>. Registration happens at startup, not on the hot path.
    /// </summary>
    private ConcurrentQueue<Job>[] _injectionQueues = [];

    internal const int MaxWorkerCount = 127;

    internal WorkerPool(int workerCount = -1)
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

    internal int WorkerCount => _workerCount;

    // ── Injection queue registry ─────────────────────────────────────────────

    /// <summary>
    /// Registers a scheduler's injection queue so workers can dequeue from it. Called once per
    /// <see cref="JobScheduler"/> during construction. Uses copy-on-write array swap.
    /// </summary>
    internal void RegisterInjectionQueue(ConcurrentQueue<Job> queue)
    {
        lock (_allContexts)
        {
            var old = _injectionQueues;
            var next = new ConcurrentQueue<Job>[old.Length + 1];
            old.CopyTo(next, 0);
            next[old.Length] = queue;
            Volatile.Write(ref _injectionQueues, next);
        }
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

        if (this.TryStealAndExecute(context))
            return true;

        return this.TryDequeueInjected(context);
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

    private bool TryDequeueInjected(WorkerContext context)
    {
        var queues = Volatile.Read(ref _injectionQueues);

        foreach (var queue in queues)
        {
            if (queue.TryDequeue(out var job))
            {
                this.ExecuteJob(job, context);
                return true;
            }
        }

        return false;
    }

    private void ExecuteJob(Job job, WorkerContext context)
    {
        var scheduler = job._scheduler!;
        context._currentScheduler = scheduler;

#if DEBUG
        job._state = JobState.Executing;
#endif

        job.Execute(context);

#if DEBUG
        job._state = JobState.Completed;
#endif

        this.PropagateCompletion(job, context);
        scheduler.DecrementOutstanding();

        context._currentScheduler = null;
    }

    private void PropagateCompletion(Job job, WorkerContext context)
    {
        if (job._dependents is not { } dependents)
            return;

        var scheduler = context._currentScheduler!;

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
            dependent._scheduler = scheduler;
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
