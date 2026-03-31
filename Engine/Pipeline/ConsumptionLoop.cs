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
///   The loop does not call the consumer until two distinct nodes exist
///   (_previous and _latest are both non-null and distinct).  This means
///   the consumer always has a valid interpolation range — no null checks
///   needed.
///
/// EPOCH ADVANCEMENT
///   The epoch is set to _previous.SequenceNumber.  Cleanup frees strictly
///   below the epoch, so both _previous (sequence N, not &lt; N) and _latest
///   (sequence &gt; N) remain alive — along with the entire chain between them.
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
internal sealed class ConsumptionLoop<TPayload, TConsumer, TClock, TWaiter>(
    SharedState<TPayload> shared,
    TConsumer consumer,
    TClock clock,
    TWaiter waiter,
    DeferredConsumerRegistration<TPayload>[] deferredConsumers)
    where TConsumer : struct, IConsumer<TPayload>
    where TClock : struct, IClock
    where TWaiter : struct, IWaiter
{
    private readonly SharedState<TPayload> _shared = shared;
    private readonly TClock _clock = clock;
    private readonly TWaiter _waiter = waiter;
    private readonly DeferredConsumerScheduler _deferred = new(shared.PinnedVersions, deferredConsumers);
    private TConsumer _consumer = consumer;

    private BaseProductionLoop<TPayload>.ChainNode? _previous;
    private BaseProductionLoop<TPayload>.ChainNode? _latest;

    public void Run(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (!cancellationToken.IsCancellationRequested)
            this.RunOneIteration(cancellationToken);
    }

    private void RunOneIteration(CancellationToken cancellationToken)
    {
        var frameStart = _clock.NowNanoseconds;

        _deferred.EnsureScheduleInitialized(frameStart);
        _deferred.DrainCompletedTasks();

        var latestNode = _shared.LatestNode;
        if (latestNode is not null)
        {
            if (!ReferenceEquals(latestNode, _latest))
            {
                _previous = _latest;
                _latest = latestNode;
            }

            if (_previous is not null)
            {
                _deferred.MaybeRunConsumers(latestNode, frameStart, cancellationToken);

                _consumer.Consume(
                    _previous,
                    latestNode,
                    frameStart,
                    cancellationToken);

                _shared.ConsumptionEpoch = _previous.SequenceNumber;
            }
        }

        this.ThrottleToFrameRate(frameStart, cancellationToken);
    }

    // ── Frame throttle ───────────────────────────────────────────────────────

    private void ThrottleToFrameRate(long frameStart, CancellationToken cancellationToken)
    {
        var elapsed = Math.Max(0, _clock.NowNanoseconds - frameStart);
        var remaining = SimulationConstants.RenderIntervalNanoseconds - elapsed;
        if (remaining > 0)
            _waiter.Wait(new TimeSpan(remaining / 100), cancellationToken);
    }

    // ── Deferred consumer scheduler ──────────────────────────────────────────

    /// <summary>
    /// Encapsulates all state and logic for scheduling and dispatching deferred
    /// consumers (e.g. periodic saves).  Keeps the main consumption loop lean.
    /// </summary>
    private sealed class DeferredConsumerScheduler
    {
        private readonly PinnedVersions _pinnedVersions;
        private readonly IDeferredConsumer<TPayload>[] _consumers;
        private readonly long[] _initialDelays;
        private readonly Task<long>?[] _inFlightTasks;
        private readonly int[] _pinnedSequences;
        private readonly PriorityQueue<int, long> _schedule;
        private bool _initialized;

        public DeferredConsumerScheduler(
            PinnedVersions pinnedVersions,
            DeferredConsumerRegistration<TPayload>[] registrations)
        {
            _pinnedVersions = pinnedVersions;

            var count = registrations.Length;
            _consumers = new IDeferredConsumer<TPayload>[count];
            _initialDelays = new long[count];
            _inFlightTasks = new Task<long>?[count];
            _pinnedSequences = new int[count];
            _schedule = new PriorityQueue<int, long>(count);

            for (var i = 0; i < count; i++)
            {
                _consumers[i] = registrations[i].Consumer;
                _initialDelays[i] = registrations[i].InitialDelayNanoseconds;
            }
        }

        public void EnsureScheduleInitialized(long nowNanoseconds)
        {
            if (_initialized)
                return;
            _initialized = true;

            for (var i = 0; i < _consumers.Length; i++)
                _schedule.Enqueue(i, nowNanoseconds + _initialDelays[i]);
        }

        public void DrainCompletedTasks()
        {
            for (var i = 0; i < _inFlightTasks.Length; i++)
            {
                var task = _inFlightTasks[i];
                if (task is null || !task.IsCompleted)
                    continue;

                _pinnedVersions.Unpin(_pinnedSequences[i], _consumers[i]);
                _inFlightTasks[i] = null;

                var nextRunTime = task.IsCompletedSuccessfully ? task.Result : 0L;
                _schedule.Enqueue(i, nextRunTime);
            }
        }

        public void MaybeRunConsumers(
            BaseProductionLoop<TPayload>.ChainNode latest,
            long frameStartNanoseconds,
            CancellationToken cancellationToken)
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

                var seq = latest.SequenceNumber;
                _pinnedVersions.Pin(seq, _consumers[consumerIndex]);
                _pinnedSequences[consumerIndex] = seq;

                try
                {
                    _inFlightTasks[consumerIndex] = _consumers[consumerIndex]
                        .ConsumeAsync(latest.Payload, seq, cancellationToken);
                }
                catch
                {
                    _pinnedVersions.Unpin(seq, _consumers[consumerIndex]);
                    _schedule.Enqueue(consumerIndex, 0);
                    throw;
                }
            }
        }
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
