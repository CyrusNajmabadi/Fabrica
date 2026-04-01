using Engine.Threading;

namespace Engine.Pipeline;

/// <summary>
/// The consumption ("consumer") thread. Processes the latest node at ≈60 fps and coordinates deferred consumers (e.g. periodic
/// saves) via a min-heap schedule.
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
///   The loop holds two distinct node references: _previous and _latest. When LatestNode changes (new ticks published), the pair
///   rotates: old latest becomes previous, new node becomes latest. Between rotations the pair is stable.
///
///   The full forward-linked chain from _previous to _latest is guaranteed alive during the Consume call. When production
///   publishes multiple nodes between frames, _previous and _latest may be several sequences apart — the consumer can iterate
///   every intermediate node via the chain, or simply work with the two endpoints.
///
///   The loop does not call the consumer until two distinct nodes exist (_previous and _latest are both non-null and distinct).
///   This means the consumer always has a valid interpolation range — no null checks needed.
///
/// EPOCH ADVANCEMENT
///   The epoch is set to _previous.SequenceNumber. Cleanup frees strictly below the epoch, so both _previous (sequence N, not
///   &lt; N) and _latest (sequence &gt; N) remain alive — along with the entire chain between them.
///
/// DEFERRED CONSUMER SCHEDULING
///   Deferred consumers are managed by <see cref="DeferredConsumerScheduler"/> using a min-heap keyed by next-run timestamp. The
///   hot-path check is O(1) — a single peek determines if any consumer is due. See <see cref="PinnedVersions"/> for the full
///   pinning protocol that prevents the production thread from reclaiming nodes held by in-flight deferred tasks.
///
/// Generic constraints (all struct) eliminate interface dispatch on every call in the hot frame loop.
/// </summary>
internal sealed partial class ConsumptionLoop<TPayload, TConsumer, TClock, TWaiter>(
    SharedPipelineState<TPayload> shared,
    TConsumer consumer,
    TClock clock,
    TWaiter waiter,
    IDeferredConsumer<TPayload>[] deferredConsumers)
    where TConsumer : struct, IConsumer<TPayload>
    where TClock : struct, IClock
    where TWaiter : struct, IWaiter
{
    private readonly SharedPipelineState<TPayload> _shared = shared;
    private readonly TClock _clock = clock;
    private readonly TWaiter _waiter = waiter;
    private readonly DeferredConsumerScheduler _deferred = new(shared.PinnedVersions, deferredConsumers);
#pragma warning disable IDE0044 // Mutable struct — readonly would cause defensive copies
    private TConsumer _consumer = consumer;
#pragma warning restore IDE0044

    private BaseProductionLoop<TPayload>.ChainNode? _previous;
    private BaseProductionLoop<TPayload>.ChainNode? _latest;

    public void Run(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (!cancellationToken.IsCancellationRequested)
            this.RunOneIteration(cancellationToken);
    }

    /// <summary>
    /// Single frame of the consumption loop.
    ///
    /// Housekeeping first: finalise any deferred tasks that completed since last frame (unpin, re-schedule), then check whether
    /// the production thread has published a new node. If so, rotate the previous/latest pair so the consumer always sees the two
    /// most-recent distinct nodes.
    ///
    /// Once we have a valid pair, dispatch due deferred consumers (O(1) peek on the min-heap — no work when nothing is scheduled)
    /// and then hand the pair to the fast consumer. Finally, advance the epoch to keep both held nodes alive and sleep for any
    /// remaining frame budget.
    /// </summary>
    private void RunOneIteration(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frameStart = _clock.NowNanoseconds;

        _deferred.EnsureScheduleInitialized(frameStart);
        _deferred.DrainCompletedTasks(frameStart);

        var latestNode = _shared.LatestNode;
        if (latestNode is not null)
        {
            if (!ReferenceEquals(latestNode, _latest))
            {
                _previous = _latest;
                _latest = latestNode;
            }

            // Clamp: if the production thread published between our clock read and the volatile read of LatestNode, frameStart
            // could precede the node's publish time. Clamping to zero-elapsed prevents negative interpolation values.
            frameStart = Math.Max(frameStart, latestNode.PublishTimeNanoseconds);

            if (_previous is not null)
            {
                _deferred.MaybeRunConsumers(latestNode, frameStart, cancellationToken);

                _consumer.Consume(
                    _previous,
                    latestNode,
                    frameStart,
                    cancellationToken);

                // Epoch = _previous.SequenceNumber, not _latest: cleanup frees sequence
                // < epoch, so _previous (N) survives (N is not < N). We can't advance to _latest because _previous is reused on the next frame if no new node arrives — freeing it would be UAF.
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
}
