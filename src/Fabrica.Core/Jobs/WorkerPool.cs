using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fabrica.Core.Threading.Queues;

namespace Fabrica.Core.Jobs;

/// <summary>
/// Shared pool of worker threads backed by per-worker <see cref="BoundedLocalQueue{T}"/>. Multiple
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
///   - Background workers: managed by the pool, loop in <see cref="RunWorker"/> through a 3-phase
///     idle strategy (HotSpin → WarmYield → Parked). When truly idle, they park on per-worker
///     <see cref="ManualResetEventSlim"/> events.
///
///   - Coordinators: external threads (e.g. the game loop) that call <see cref="JobScheduler.Submit"/>.
///     They run <see cref="TryExecuteOne"/> in a spin loop (<see cref="JobScheduler"/>'s
///     RunUntilComplete) and NEVER park. This is critical for progress — at least one coordinator
///     is always runnable while a DAG is in flight.
///
/// ADAPTIVE PARKING (3-PHASE IDLE STRATEGY)
///
///   Adapted from Tokio's "notify then search" pattern (see idle.rs and worker.rs in
///   <see href="https://github.com/tokio-rs/tokio/tree/master/tokio/src/runtime/scheduler/multi_thread"/>).
///
///   Background worker loop (<see cref="RunWorker"/>):
///
///     Phase 0 — Running:
///       <see cref="TryExecuteOne"/> — pop own deque, steal from peers, drain injection queue.
///       If work is found, execute and repeat. If this worker was searching, transition out and
///       potentially cascade-wake one parked worker (see CASCADE WAKE below).
///
///     Phase 1 — HotSpin:
///       Pure CPU spin via <see cref="SpinWait"/> for ~10 iterations (~sub-microsecond). Catches
///       most inter-phase DAG gaps where the next batch of work arrives within microseconds.
///       Worker is counted as "searching" in <see cref="_idleState"/>.
///
///     Phase 2 — WarmYield:
///       <see cref="Thread.Yield()"/> loop for up to <see cref="KeepAliveMs"/> (1000ms default).
///       Gives up the timeslice but stays on the OS run queue. Much cheaper than HotSpin but
///       still responsive within one yield-reschedule cycle. During active gameplay (work every
///       ~16ms), workers never leave this phase. Worker is still counted as "searching".
///
///     Phase 3 — Parked:
///       Blocked on a per-worker <see cref="ManualResetEventSlim"/>. Zero CPU. For truly idle
///       periods (loading screen, pause menu, low-demand game states). Once woken, the worker
///       returns to Running and the full keepAlive window resets.
///
/// CASCADE WAKE POLICY
///
///   <see cref="_idleState"/> packs two counters: numUnparked (workers not in Parked state) and
///   numSearching (workers in HotSpin or WarmYield, actively looking for work).
///
///   When new work arrives (<see cref="TryWakeOneWorker"/>, called by <see cref="PropagateCompletion"/>
///   and <see cref="NotifyWorkAvailable"/>):
///     - If numSearching > 0: do nothing. A searching worker will find the work on its next
///       <see cref="TryExecuteOne"/> call.
///     - If numSearching == 0 and numUnparked &lt; _backgroundWorkerCount: wake exactly ONE parked
///       worker by popping from <see cref="_sleepers"/> and setting its event.
///
///   When a worker finds work and leaves searching (<see cref="TransitionFromSearching"/>):
///     - If it was the last searcher (numSearching was 1), call <see cref="TryWakeOneWorker"/>
///       to cascade-wake one more parked worker. This ensures at least one searcher exists while
///       surplus work is available. The chain naturally stops when a woken worker searches and
///       finds nothing.
///
/// CORRECTNESS ARGUMENT (NO LOST WAKES)
///
///   The announce-then-recheck pattern is preserved:
///
///   Before parking, the worker decrements numUnparked in <see cref="_idleState"/> (announce) and
///   pushes itself to <see cref="_sleepers"/>, then rechecks <see cref="TryExecuteOne"/>. If a
///   producer published work before the recheck, the worker finds it and undoes the park. If a
///   producer publishes work after the recheck, it sees numSearching == 0 and numUnparked &lt; total,
///   pops from _sleepers, and wakes the worker via its event.
///
///   Workers in HotSpin or WarmYield are counted as searching (numSearching > 0). A producer
///   seeing numSearching > 0 does not wake anyone — the searching worker will find the work on
///   its next TryExecuteOne call. This is safe because searching workers poll continuously.
///
/// COORDINATOR PROGRESS GUARANTEE
///
///   Even if every background worker is parked, at least one coordinator thread is running the
///   TryExecuteOne spin loop while its DAG has outstanding jobs. The coordinator can pop, steal,
///   and drain injection — it is a full participant in work execution.
///
/// SHUTDOWN
///
///   <see cref="Dispose"/> sets _shutdownRequested = true, then sets all per-worker events to wake
///   parked workers, then Joins all threads. Workers check _shutdownRequested at the top of each
///   loop iteration and in the WarmYield phase, then exit.
///
/// REFERENCE: Tokio scheduler, <see href="https://github.com/tokio-rs/tokio"/>, MIT License.
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
    /// Packed idle state: <c>(numUnparked &lt;&lt; 16) | numSearching</c>. Atomically tracks how many
    /// background workers are unparked (Running + HotSpin + WarmYield) and how many are currently
    /// searching (HotSpin + WarmYield). Adapted from Tokio's <c>idle::State</c>.
    /// </summary>
    private int _idleState;

    /// <summary>Per-worker park/wake events, one per background worker.</summary>
    private readonly ManualResetEventSlim[] _workerEvents;

    /// <summary>
    /// Stack of background worker indices currently parked. Producers pop from here to wake a
    /// specific worker. Lock-free via <see cref="ConcurrentStack{T}"/>.
    /// </summary>
    private readonly ConcurrentStack<int> _sleepers = new();

    /// <summary>
    /// How long (ms) a worker stays in WarmYield before parking. During active gameplay (work
    /// every ~16ms), workers never leave this phase. Set to 1000ms so workers park after ~1s of
    /// true idleness (loading screens, menus, low-demand game states).
    /// </summary>
    internal const int KeepAliveMs = 1000;

    /// <summary>Set to <c>true</c> during <see cref="Dispose"/> to break worker loops.</summary>
    private volatile bool _shutdownRequested;

    /// <summary>Guard for idempotent <see cref="Dispose"/>.</summary>
    private int _disposed;

    /// <summary>
    /// Shared injection queue. Serves dual purpose: (1) overflow target when a worker's local
    /// <see cref="BoundedLocalQueue{T}"/> is full, and (2) landing zone for top-level jobs injected
    /// via <see cref="JobScheduler.TestAccessor.Inject"/>. The coordinator fast-path
    /// (<see cref="JobScheduler.Submit"/>) pushes directly onto the coordinator's deque instead.
    /// </summary>
    private readonly StrongBox<InjectionQueue<Job>> _injectionQueue = new(new InjectionQueue<Job>());

    /// <summary>Next coordinator slot index to hand out via <see cref="AttachCoordinator"/>.</summary>
    private int _nextCoordinatorSlot;

    internal const int MaxWorkerCount = 127;

    public WorkerPool(int workerCount = -1, int coordinatorCount = 0)
    {
        if (workerCount < 0)
            workerCount = Math.Max(1, Math.Min(Threading.ProcessorTopology.PerformanceCoreCount - coordinatorCount, MaxWorkerCount));

        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(coordinatorCount, 0);

        var totalCount = workerCount + coordinatorCount;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalCount, MaxWorkerCount);

        _backgroundWorkerCount = workerCount;
        _totalWorkerCount = totalCount;

        _workerEvents = new ManualResetEventSlim[workerCount];
        for (var i = 0; i < workerCount; i++)
            _workerEvents[i] = new ManualResetEventSlim(false);

        _allContexts = new WorkerContext[totalCount];
        for (var i = 0; i < totalCount; i++)
            _allContexts[i] = new WorkerContext(this, i, _injectionQueue);

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
        _injectionQueue.Value.Enqueue(job);
        this.NotifyWorkAvailable();
    }

    /// <summary>
    /// Signals that work is available. Wakes at most one parked worker, but only if no worker is
    /// currently searching (HotSpin/WarmYield) — a searching worker will find the new work on its
    /// next <see cref="TryExecuteOne"/> call.
    /// </summary>
    internal void NotifyWorkAvailable() => this.TryWakeOneWorker();

    // ── Worker loop ─────────────────────────────────────────────────────────

    [SkipLocalsInit]
    private void RunWorker(WorkerContext context)
    {
        this.IncrementUnparked(searching: false);
        try
        {
            while (!_shutdownRequested)
            {
                // ── Running: pop own deque / steal / injection ──
                if (this.TryExecuteOne(context))
                {
                    this.TransitionFromSearching(context);
                    continue;
                }

                // ── HotSpin: ~10 pure CPU spin iterations ──
                this.EnterSearching(context);
                var spinWait = new SpinWait();
                var found = false;
                while (!spinWait.NextSpinWillYield)
                {
                    spinWait.SpinOnce();
                    if (this.TryExecuteOne(context))
                    {
                        found = true;
                        break;
                    }
                }

                if (found)
                {
                    this.TransitionFromSearching(context);
                    continue;
                }

                // ── WarmYield: Thread.Yield() for up to KeepAliveMs ──
                var deadline = Environment.TickCount64 + KeepAliveMs;
                while (Environment.TickCount64 < deadline)
                {
                    Thread.Yield();
                    if (_shutdownRequested)
                        return;

                    if (this.TryExecuteOne(context))
                    {
                        found = true;
                        break;
                    }
                }

                if (found)
                {
                    this.TransitionFromSearching(context);
                    continue;
                }

                // ── Park: deep sleep until explicitly woken ──
                this.LeaveSearching(context);
                this.TransitionToParked(context);

                // Announce-then-recheck: we just decremented numUnparked and pushed to _sleepers.
                // Re-check for work to close the race with a concurrent producer.
                if (this.TryExecuteOne(context))
                {
                    this.TransitionFromParked();
                    continue;
                }

                _workerEvents[context.WorkerIndex].Wait();
                _workerEvents[context.WorkerIndex].Reset();
                this.TransitionFromParked();
            }
        }
        finally
        {
            this.DecrementUnparked(searching: false);
        }
    }

    // ── Idle state management ────────────────────────────────────────────────
    //
    //   _idleState packs two counters into a single int:
    //     bits [31:16] = numUnparked (workers in Running, HotSpin, or WarmYield)
    //     bits [15:0]  = numSearching (workers in HotSpin or WarmYield)
    //
    //   All transitions use Interlocked.Add on the packed value, which is safe because the two
    //   halves never overflow their 16-bit range (MaxWorkerCount = 127).

    private const int UnparkedBit = 1 << 16;
    private const int SearchingBit = 1;
    private const int UnparkedMask = unchecked((int)0xFFFF0000);
    private const int SearchingMask = 0x0000FFFF;

    private static int NumSearching(int state) => state & SearchingMask;
    private static int NumUnparked(int state) => (state >> 16) & 0xFFFF;

    private void IncrementUnparked(bool searching)
    {
        var delta = searching ? UnparkedBit | SearchingBit : UnparkedBit;
        Interlocked.Add(ref _idleState, delta);
    }

    private void DecrementUnparked(bool searching)
    {
        var delta = searching ? UnparkedBit | SearchingBit : UnparkedBit;
        Interlocked.Add(ref _idleState, -delta);
    }

    private void EnterSearching(WorkerContext context)
    {
        if (context.IsSearching)
            return;

        context.IsSearching = true;
        Interlocked.Add(ref _idleState, SearchingBit);
    }

    private void LeaveSearching(WorkerContext context)
    {
        if (!context.IsSearching)
            return;

        context.IsSearching = false;
        Interlocked.Add(ref _idleState, -SearchingBit);
    }

    /// <summary>
    /// Transitions a worker from searching to running after finding work. If this worker was the
    /// last searcher, cascade-wakes one parked worker to maintain the search chain.
    /// </summary>
    private void TransitionFromSearching(WorkerContext context)
    {
        if (!context.IsSearching)
            return;

        context.IsSearching = false;
        var prev = Interlocked.Add(ref _idleState, -SearchingBit);
        var wasLastSearcher = NumSearching(prev) == 1;

        if (wasLastSearcher)
            this.TryWakeOneWorker();
    }

    private void TransitionToParked(WorkerContext context)
    {
        Debug.Assert(!context.IsSearching);
        _sleepers.Push(context.WorkerIndex);
        Interlocked.Add(ref _idleState, -UnparkedBit);
    }

    private void TransitionFromParked() =>
        Interlocked.Add(ref _idleState, UnparkedBit);

    /// <summary>
    /// Wakes at most one parked worker if no worker is currently searching. This is the core of
    /// Tokio's "notify then search" pattern: a searching worker will find new work on its own, so
    /// waking a parked worker would be wasteful.
    /// </summary>
    private void TryWakeOneWorker()
    {
        var state = Volatile.Read(ref _idleState);
        if (NumSearching(state) > 0)
            return;

        if (NumUnparked(state) >= _backgroundWorkerCount)
            return;

        if (_sleepers.TryPop(out var workerIndex))
        {
            Interlocked.Add(ref _idleState, UnparkedBit | SearchingBit);
            _workerEvents[workerIndex].Set();
        }
    }

    // ── Core execution ──────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to find and execute one job. Tries local deque, then steals from peers, then
    /// checks the injection queue. Called by both background workers and coordinator threads.
    /// </summary>
    [SkipLocalsInit]
    internal bool TryExecuteOne(WorkerContext context)
    {
        // WORK DISCOVERY PRIORITY: (1) pop own deque — LIFO, cache-hot; (2) steal from peers — FIFO; (3) shared injection
        // queue — cold, after local and peer deques (JobScheduler.Submit enqueues via Inject).
        var job = context.Deque.TryPop();
        if (job != null)
        {
#if INSTRUMENT
            context.LastJobSource = JobSource.Local;
#endif
            this.ExecuteJob(job, context);
            return true;
        }

        if (this.TryStealAndExecute(context))
            return true;

        return this.TryDequeueInjected(context);
    }

    [SkipLocalsInit]
    private bool TryStealAndExecute(WorkerContext context)
    {
        var count = _allContexts.Length;
        var start = (int)context.StealRand.NextN((uint)count);

        for (var i = 0; i < count; i++)
        {
            var idx = start + i;
            if (idx >= count) idx -= count;
            var target = _allContexts[idx];
            if (target.WorkerIndex == context.WorkerIndex)
                continue;

            // TryStealHalf: batch-steal ~half the victim's items into our local queue,
            // returning one item for immediate execution. The remaining stolen items
            // become available via our own TryPop on subsequent iterations, avoiding
            // repeated cross-core steal overhead.
            var job = target.Deque.TryStealHalf(ref context.Deque);
            if (job != null)
            {
#if INSTRUMENT
                context.LastJobSource = JobSource.Steal;
#endif
                this.ExecuteJob(job, context);
                return true;
            }
        }

        return false;
    }

    private bool TryDequeueInjected(WorkerContext context)
    {
        var job = _injectionQueue.Value.TryDequeue();
        if (job != null)
        {
#if INSTRUMENT
            context.LastJobSource = JobSource.Injection;
#endif
            this.ExecuteJob(job, context);
            return true;
        }

        return false;
    }

    private void ExecuteJob(Job job, WorkerContext context)
    {
#if INSTRUMENT
        long obtainedTs = Stopwatch.GetTimestamp();
#endif

        var scheduler = job.Scheduler!;
        context.CurrentScheduler = scheduler;

#if DEBUG
        job.State = JobState.Executing;
#endif

        job.Execute(new JobContext(context));

#if DEBUG
        job.State = JobState.Completed;
#endif

        if (job.Dependents.Count > 0)
            this.PropagateCompletion(job, context);

#if INSTRUMENT
        // Record BEFORE DecrementOutstanding: the coordinator exits RunUntilComplete
        // when _outstandingJobs hits 0, then reads these records. If we recorded after
        // the decrement, a worker could still be writing when the coordinator reads.
        this.RecordInstrumentation(context, obtainedTs);
#endif

        scheduler.DecrementOutstanding();
        // CurrentScheduler is intentionally NOT nulled here. The next ExecuteJob will overwrite it,
        // and nulling would cost a GC write barrier (~2ns) on every job for no functional benefit.
        // Enqueue() is only called during Job.Execute, where CurrentScheduler is always valid.
    }

#if INSTRUMENT
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RecordInstrumentation(WorkerContext context, long obtainedTs)
    {
        var records = context.InstrumentRecords;
        if (records == null)
            return;

        var idx = context.InstrumentRecordCount;
        if (idx < records.Length)
        {
            ref var rec = ref records[idx];
            rec.ObtainedTs = obtainedTs;
            rec.CompletedTs = Stopwatch.GetTimestamp();
            rec.Source = context.LastJobSource;
            rec.WorkerIndex = (byte)context.WorkerIndex;
            context.InstrumentRecordCount = idx + 1;
        }
    }
#endif

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PropagateCompletion(Job job, WorkerContext context)
    {
        var dependents = job.Dependents;
        var count = dependents.Count;
        var scheduler = context.CurrentScheduler!;
        var readied = 0;

        // Bitfield tracking which dependents this thread readied (decremented to 0), so
        // the second pass pushes exactly the right set. Necessary because concurrent
        // threads may also be decrementing shared dependents.
        Span<long> readiedBits = stackalloc long[(count + 63) >> 6];

        for (var i = 0; i < count; i++)
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
            readiedBits[i >> 6] |= 1L << (i & 63);
            readied++;
        }

        if (readied == 0)
            return;

        // Single atomic increment for all readied dependents, before any are pushed.
        scheduler.IncrementOutstandingBy(readied);

        for (var i = 0; i < count; i++)
        {
            if ((readiedBits[i >> 6] & (1L << (i & 63))) != 0)
                context.Deque.Push(dependents[i]);
        }

        this.TryWakeOneWorker();
    }

    // ── Disposal ────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdownRequested = true;

        // Wake all parked workers so they observe _shutdownRequested and exit.
        foreach (var evt in _workerEvents)
            evt.Set();

        foreach (var thread in _workerThreads)
            thread.Join();

        foreach (var evt in _workerEvents)
            evt.Dispose();
    }

    // ── Test accessor ───────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor(WorkerPool pool)
    {
        public WorkerContext GetWorkerContext(int index) => pool._allContexts[index];
        public int BackgroundWorkerCount => pool._backgroundWorkerCount;
        public int WorkerCount => pool._totalWorkerCount;

        /// <summary>
        /// Allocates per-worker instrumentation buffers. Each worker can record up to
        /// <paramref name="maxEventsPerWorker"/> job executions per instrumentation session.
        /// </summary>
        public void EnableInstrumentation(int maxEventsPerWorker)
        {
            foreach (var ctx in pool._allContexts)
            {
                ctx.InstrumentRecords = new SchedulerRecord[maxEventsPerWorker];
                ctx.InstrumentRecordCount = 0;
            }
        }

        /// <summary>Disables instrumentation and releases buffers.</summary>
        public void DisableInstrumentation()
        {
            foreach (var ctx in pool._allContexts)
            {
                ctx.InstrumentRecords = null;
                ctx.InstrumentRecordCount = 0;
            }
        }

        /// <summary>Resets all per-worker record counts to zero without reallocating buffers.</summary>
        public void ResetInstrumentation()
        {
            foreach (var ctx in pool._allContexts)
                ctx.InstrumentRecordCount = 0;
        }

        /// <summary>
        /// Returns a snapshot of all instrumentation records across all workers. Each record
        /// carries its worker index. Sorted by <see cref="SchedulerRecord.ObtainedTs"/>.
        /// </summary>
        public SchedulerRecord[] GetInstrumentationRecords()
        {
            var contexts = pool._allContexts;
            var counts = new int[contexts.Length];
            var total = 0;
            for (var i = 0; i < contexts.Length; i++)
            {
                counts[i] = contexts[i].InstrumentRecordCount;
                total += counts[i];
            }

            var result = new SchedulerRecord[total];
            var offset = 0;
            for (var i = 0; i < contexts.Length; i++)
            {
                var count = counts[i];
                if (count > 0)
                {
                    Array.Copy(contexts[i].InstrumentRecords!, 0, result, offset, count);
                    offset += count;
                }
            }

            Array.Sort(result, (a, b) => a.ObtainedTs.CompareTo(b.ObtainedTs));
            return result;
        }

        /// <summary>Returns raw per-worker records (not sorted, not merged).</summary>
        public (SchedulerRecord[] Records, int Count)[] GetPerWorkerRecords()
        {
            var result = new (SchedulerRecord[], int)[pool._totalWorkerCount];
            for (var i = 0; i < pool._totalWorkerCount; i++)
            {
                var ctx = pool._allContexts[i];
                result[i] = (ctx.InstrumentRecords ?? [], ctx.InstrumentRecordCount);
            }

            return result;
        }
    }
}
