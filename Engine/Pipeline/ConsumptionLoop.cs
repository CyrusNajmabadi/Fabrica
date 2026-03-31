using Engine;
using Engine.Memory;
using Engine.Threading;

namespace Engine.Pipeline;

/// <summary>
/// The consumption ("consumer") thread.  Processes the latest node at ≈60 fps
/// and coordinates deferred consumers (e.g. periodic saves) via a min-heap schedule.
///
/// FRAME LOOP  (RunOneIteration)
///   1. DrainCompletedDeferredTasks: unpin nodes whose deferred tasks have finished
///      and re-insert the consumer into the schedule with its next run time.
///   2. Volatile-read LatestNode (acquire fence: all payload writes by the
///      production thread are now visible).
///   3. Rotate the node pair if a new node has arrived (see below).
///   4. MaybeRunDeferredConsumers: peek the schedule heap — if the head entry's
///      time ≤ now, pop, pin, and dispatch.  O(1) when nothing is due.
///   5. Consume: the consumer processes the previous/latest pair for the
///      duration of the call only — it must not store them.
///   6. ConsumptionEpoch = _previous.SequenceNumber (or _latest's if no
///      previous exists yet).  This keeps both held nodes alive.
///   7. ThrottleToFrameRate: sleep for any remaining frame budget (≈16.67 ms).
///
/// PREVIOUS / LATEST MODEL
///   The loop holds two distinct node references: _previous and _latest.
///   When LatestNode changes (new ticks published), the pair rotates:
///   old latest becomes previous, new node becomes latest.  Between
///   rotations the pair is stable.
///
///   The full forward-linked chain from _previous to _latest is guaranteed
///   alive during the Consume call.  When production publishes multiple
///   nodes between frames, _previous and _latest may be several sequences
///   apart — the consumer can iterate every intermediate node via the
///   chain, or simply work with the two endpoints.
///
///   INVARIANT: when _previous is non-null, it is always a different object
///   reference from _latest.  The rotation guard guarantees this.
///
/// EPOCH ADVANCEMENT
///   The epoch is set to _previous.SequenceNumber when both nodes exist,
///   or _latest.SequenceNumber on the first frame (no previous yet).
///   Cleanup frees strictly below the epoch, so both _previous (sequence N,
///   not &lt; N) and _latest (sequence &gt; N) remain alive — along with the
///   entire chain between them.
///
/// DEFERRED CONSUMER SCHEDULING
///   Deferred consumers are stored in a flat array.  A PriorityQueue maps
///   consumer indices to their next-run wall-clock timestamp.  Each frame:
///     - Peek O(1): if nothing is due, skip entirely — no virtual calls.
///     - Pop due entries, pin the latest node, call ConsumeAsync.
///     - Track the in-flight Task per consumer; skip consumers whose prior
///       task is still running.
///     - When tasks complete, unpin, read the returned next-run-time, and
///       re-enqueue into the heap.
///
/// Generic constraints (all struct) eliminate interface dispatch on every call
/// in the hot frame loop.
/// </summary>
internal sealed class ConsumptionLoop<TPayload, TConsumer, TClock, TWaiter>
    where TConsumer : struct, IConsumer<TPayload>
    where TClock : struct, IClock
    where TWaiter : struct, IWaiter
{
    private ChainNode<TPayload>? _previous;
    private ChainNode<TPayload>? _latest;

    private readonly PinnedVersions _pinnedVersions;
    private readonly SharedState<TPayload> _shared;
    private TConsumer _consumer;
    private readonly TClock _clock;
    private readonly TWaiter _waiter;

    // ── Deferred consumer infrastructure ─────────────────────────────────────

    private readonly IDeferredConsumer<TPayload>[] _deferredConsumers;
    private readonly long[] _initialDelays;
    private readonly Task<long>?[] _inFlightTasks;
    private readonly int[] _pinnedSequences;
    private readonly PriorityQueue<int, long> _schedule;
    private bool _scheduleInitialized;

    public ConsumptionLoop(
        PinnedVersions pinnedVersions,
        SharedState<TPayload> shared,
        TConsumer consumer,
        TClock clock,
        TWaiter waiter,
        DeferredConsumerRegistration<TPayload>[] deferredConsumers)
    {
        _pinnedVersions = pinnedVersions;
        _shared = shared;
        _consumer = consumer;
        _clock = clock;
        _waiter = waiter;

        var count = deferredConsumers.Length;
        _deferredConsumers = new IDeferredConsumer<TPayload>[count];
        _initialDelays = new long[count];
        _inFlightTasks = new Task<long>?[count];
        _pinnedSequences = new int[count];
        _schedule = new PriorityQueue<int, long>(count);

        for (var i = 0; i < count; i++)
        {
            _deferredConsumers[i] = deferredConsumers[i].Consumer;
            _initialDelays[i] = deferredConsumers[i].InitialDelayNanoseconds;
        }
    }

    public void Run(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (!cancellationToken.IsCancellationRequested)
            this.RunOneIteration(cancellationToken);
    }

    private void RunOneIteration(CancellationToken cancellationToken)
    {
        var frameStart = _clock.NowNanoseconds;

        this.EnsureScheduleInitialized(frameStart);
        this.DrainCompletedDeferredTasks();

        var latestNode = _shared.LatestNode;
        if (latestNode is not null)
        {
            if (!ReferenceEquals(latestNode, _latest))
            {
                _previous = _latest;
                _latest = latestNode;
            }

            this.MaybeRunDeferredConsumers(frameStart, cancellationToken);

            _consumer.Consume(
                _previous,
                _latest!,
                frameStart,
                cancellationToken);

            _shared.ConsumptionEpoch = _previous?.SequenceNumber
                                       ?? _latest!.SequenceNumber;
        }

        this.ThrottleToFrameRate(frameStart, cancellationToken);
    }

    // ── Deferred consumer scheduling ─────────────────────────────────────────

    private void EnsureScheduleInitialized(long nowNanoseconds)
    {
        if (_scheduleInitialized)
            return;
        _scheduleInitialized = true;

        for (var i = 0; i < _deferredConsumers.Length; i++)
            _schedule.Enqueue(i, nowNanoseconds + _initialDelays[i]);
    }

    private void DrainCompletedDeferredTasks()
    {
        for (var i = 0; i < _inFlightTasks.Length; i++)
        {
            var task = _inFlightTasks[i];
            if (task is null || !task.IsCompleted)
                continue;

            _pinnedVersions.Unpin(_pinnedSequences[i], _deferredConsumers[i]);
            _inFlightTasks[i] = null;

            long nextRunTime;
            if (task.IsCompletedSuccessfully)
            {
                nextRunTime = task.Result;
            }
            else
            {
                // On failure, reschedule immediately (retry next frame).
                nextRunTime = 0;
            }

            _schedule.Enqueue(i, nextRunTime);
        }
    }

    private void MaybeRunDeferredConsumers(long frameStartNanoseconds, CancellationToken cancellationToken)
    {
        while (_schedule.TryPeek(out var consumerIndex, out var nextRun))
        {
            if (nextRun > frameStartNanoseconds)
                break;

            _schedule.Dequeue();

            if (_inFlightTasks[consumerIndex] is not null)
            {
                _schedule.Enqueue(consumerIndex, nextRun);
                break;
            }

            var seq = _latest!.SequenceNumber;
            _pinnedVersions.Pin(seq, _deferredConsumers[consumerIndex]);
            _pinnedSequences[consumerIndex] = seq;

            try
            {
                _inFlightTasks[consumerIndex] = _deferredConsumers[consumerIndex]
                    .ConsumeAsync(_latest.Payload, seq, cancellationToken);
            }
            catch
            {
                _pinnedVersions.Unpin(seq, _deferredConsumers[consumerIndex]);
                _schedule.Enqueue(consumerIndex, 0);
                throw;
            }
        }
    }

    // ── Frame throttle ───────────────────────────────────────────────────────

    private void ThrottleToFrameRate(long frameStart, CancellationToken cancellationToken)
    {
        var elapsed = Math.Max(0, _clock.NowNanoseconds - frameStart);
        var remaining = SimulationConstants.RenderIntervalNanoseconds - elapsed;
        if (remaining > 0)
            _waiter.Wait(new TimeSpan(remaining / 100), cancellationToken);
    }

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor
    {
        private readonly ConsumptionLoop<TPayload, TConsumer, TClock, TWaiter> _loop;

        public TestAccessor(ConsumptionLoop<TPayload, TConsumer, TClock, TWaiter> loop) => _loop = loop;

        public void RunOneIteration(CancellationToken cancellationToken) => _loop.RunOneIteration(cancellationToken);

        public void ThrottleToFrameRate(long frameStart, CancellationToken cancellationToken) =>
            _loop.ThrottleToFrameRate(frameStart, cancellationToken);
    }
}
