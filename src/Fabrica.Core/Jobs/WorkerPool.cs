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
/// THREAD MODEL
///
///   Two kinds of threads participate in work execution:
///
///   - Background workers: managed by the pool, loop in <see cref="RunWorker"/>. When idle, they
///     park on <see cref="_workSignal"/> (a <see cref="SemaphoreSlim"/>).
///
///   - Coordinators: external threads (e.g. the game loop) that call <see cref="JobScheduler.Submit"/>.
///     They run <see cref="TryExecuteOne"/> in a spin loop (<see cref="JobScheduler"/>'s
///     RunUntilComplete) and NEVER park on the semaphore. This is critical for progress — at least
///     one coordinator is always runnable while a DAG is in flight.
///
/// PARKING AND WAKE PROTOCOL
///
///   The core invariant is: a background worker must never permanently sleep on the semaphore while
///   stealable work exists. The protocol uses announce-then-recheck to prevent lost wakes, and
///   batched notification to minimize contention.
///
///   Background worker loop (<see cref="RunWorker"/>):
///     (1) TryExecuteOne — attempt local pop, peer steal, injection dequeue.
///     (2) If (1) fails: Interlocked.Increment(_parkedWorkers) — announce intent to park.
///     (3) TryExecuteOne again — the recheck. This closes the race window between (1) and (2).
///     (4) If (3) finds work: Interlocked.Decrement(_parkedWorkers), execute, go to (1).
///     (5) If (3) finds nothing: _workSignal.Wait() — block until woken.
///     (6) On wake: Interlocked.Decrement(_parkedWorkers), go to (1).
///
///   Producer notification (<see cref="PropagateCompletion"/>):
///     (a) Push all newly-readied dependents onto the completing worker's deque.
///     (b) Read Volatile.Read(_parkedWorkers) to get a snapshot of how many workers are sleeping.
///     (c) If parked > 0: _workSignal.Release(Min(readied, parked)) — wake up to one worker per
///         readied job, but never more than are actually parked.
///
///   Single-job notification (<see cref="NotifyWorkAvailable"/>):
///     Used by <see cref="Inject"/> and <see cref="JobScheduler.Submit"/>. Reads _parkedWorkers;
///     if > 0, releases one permit.
///
/// CORRECTNESS ARGUMENT (NO LOST WAKES)
///
///   The safety argument rests on the interaction between work publication (deque Push / injection
///   Enqueue) and the announce-then-recheck protocol. We enumerate all interleavings between a
///   producer P making work visible and a worker W attempting to park.
///
///   W is in one of four states relative to its park cycle:
///
///   Case 1 — W is in step (1), scanning for work:
///     P pushes work onto a deque. W's TryExecuteOne scans all deques (own pop + peer steal +
///     injection). If the push is visible by the time W scans that deque, W finds it. If not (W
///     already passed that deque), W proceeds to (2)→(3), and the recheck at (3) will see it —
///     the push happened-before the recheck because P's push precedes P's notification, and W's
///     increment at (2) provides a full fence that orders W's subsequent reads after P's store.
///
///   Case 2 — W is between (1) and (2), about to announce:
///     W hasn't incremented _parkedWorkers yet, so P's Volatile.Read(_parkedWorkers) may not
///     count W. But W hasn't blocked yet — it will still execute (2)→(3), and the recheck at (3)
///     sees the pushed work. W never reaches Wait().
///
///   Case 3 — W is between (2) and (3), announced but rechecking:
///     W has incremented _parkedWorkers, so P's snapshot may or may not include W. Either way, W
///     is about to run TryExecuteOne at (3). The pushed work is visible (it was pushed before P
///     reads _parkedWorkers, and W's (3) runs after W's increment which is a full barrier). W
///     finds the work and decrements _parkedWorkers at (4). No lost wake.
///
///   Case 4 — W is in (5), blocked on Wait():
///     W already ran the recheck at (3) and found nothing — meaning the push had not yet happened
///     at that point. P's push happens after W's recheck. P then reads _parkedWorkers and sees W
///     (W incremented before (3), and W is still counted because decrement only happens at (4) or
///     (6)). P releases a permit, waking W. No lost wake.
///
///   In all four cases, either W discovers work before parking, or P's notification wakes W after
///   parking. There is no interleaving where W sleeps permanently with stealable work.
///
/// COORDINATOR PROGRESS GUARANTEE
///
///   Even if every background worker is parked, at least one coordinator thread is running the
///   TryExecuteOne spin loop while its DAG has outstanding jobs (Volatile.Read(_outstandingJobs)
///   > 0). The coordinator can pop, steal, and drain injection — it is a full participant in work
///   execution. This means progress is guaranteed even if notification is delayed or under-counted;
///   the coordinator will eventually steal every job that no background worker picks up.
///
/// SEMAPHORE PERMIT BOUNDS
///
///   Over-release is possible: P reads _parkedWorkers = K, but between the read and the Release
///   call, some workers wake from other notifications and decrement _parkedWorkers. The excess
///   permits cause spurious wakeups — a worker wakes, finds no work, and re-parks on the next
///   loop iteration, consuming the permit. Permits do not accumulate unboundedly because:
///   (a) Release is guarded by _parkedWorkers > 0 (no release when nobody is sleeping).
///   (b) Each spurious wake consumes one permit (Wait + re-park).
///   (c) Batched release caps at Min(readied, parked), reducing permit storms compared to
///       per-job notification.
///
/// SHUTDOWN
///
///   <see cref="Dispose"/> sets _shutdownRequested = true, then releases _backgroundWorkerCount
///   permits to wake all parked workers, then Joins all threads. Workers check _shutdownRequested
///   at the top of each loop iteration and exit. Shutdown permits do not interfere with work
///   notifications because threads exit immediately after waking.
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
            workerCount = Math.Max(1, Math.Min(Environment.ProcessorCount - coordinatorCount, MaxWorkerCount));

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
            _workerThreads[i] = Threading.ThreadPinningNative.StartNativeThreadWithHighQos(
                $"WorkerPool-Worker-{i}",
                () => this.RunWorker(ctx),
                coreIndex: i);
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
        var readied = 0;

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
            readied++;
        }

        if (readied > 0)
        {
            var parked = Volatile.Read(ref _parkedWorkers);
            if (parked > 0)
                _workSignal.Release(Math.Min(readied, parked));
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
