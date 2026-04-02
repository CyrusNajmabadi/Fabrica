using System.Diagnostics;
using Fabrica.Core.Threading;

namespace Fabrica.Pipeline;

/// <summary>
/// The consumption ("consumer") thread. Processes pipeline entries from the <see cref="Core.Collections.ProducerConsumerQueue{T}"/>
/// at ≈60 fps and coordinates deferred consumers (e.g. periodic saves) via a min-heap schedule.
///
/// FRAME LOOP  (RunOneIteration)
///   1. DrainCompletedDeferredTasks: release pins for deferred tasks that have finished and re-insert the consumer into the
///      schedule with its next run time.
///   2. ConsumerAcquire: volatile-read the producer position (acquire fence) and return a segment of all entries published since
///      the last advance.
///   3. If the segment has ≥ 2 entries (previous + at least one new), process the frame:
///      a. MaybeRunDeferredConsumers: peek the schedule heap — if the head entry's time ≤ now, pop, pin, and dispatch.
///      b. Consume: hand the full segment to the consumer. entries[0] is the previous entry (held back from last frame) and
///         entries[count-1] is the latest.
///      c. ConsumerAdvance(count - 1): advance past all entries except the last, which becomes the "previous" for the next frame.
///   4. ThrottleToFrameRate: sleep for any remaining frame budget (≈16.67 ms).
///
/// HOLD-BACK MODEL
///   The loop always holds back the last entry in the segment by advancing only <c>count - 1</c> positions. This keeps the last
///   entry's payload alive (not eligible for cleanup) so it can serve as the interpolation baseline for the next frame. On the
///   next acquire, that held-back entry appears as the first item in the new segment.
///
///   • count == 1: no new entries since last frame → skip consume (only the held-back entry from last frame).
///   • count ≥ 2: new entries arrived → consume, then advance past all but the last.
///
/// DEFERRED CONSUMER SCHEDULING
///   Deferred consumers are managed by <see cref="DeferredConsumerScheduler"/> using a min-heap keyed by next-run timestamp. The
///   hot-path check is O(1) — a single peek determines if any consumer is due. See <see cref="PinnedVersions"/> for the full
///   pinning protocol that prevents the production thread from reclaiming payloads held by in-flight deferred tasks.
///
/// Generic constraints (all struct) eliminate interface dispatch on every call in the hot frame loop.
/// </summary>
public sealed partial class ConsumptionLoop<TPayload, TConsumer, TClock, TWaiter>(
    SharedPipelineState<TPayload> shared,
    TConsumer consumer,
    TClock clock,
    TWaiter waiter,
    IDeferredConsumer<TPayload>[] deferredConsumers,
    PipelineConfiguration config)
    where TConsumer : struct, IConsumer<TPayload>
    where TClock : struct, IClock
    where TWaiter : struct, IWaiter
{
    private readonly PipelineConfiguration _config = config;
    private readonly SharedPipelineState<TPayload> _shared = shared;
    private readonly TClock _clock = clock;
    private readonly TWaiter _waiter = waiter;
    private readonly DeferredConsumerScheduler _deferred = new(shared.PinnedVersions, deferredConsumers);
#pragma warning disable IDE0044 // Mutable struct — readonly would cause defensive copies
    private TConsumer _consumer = consumer;
#pragma warning restore IDE0044

#if DEBUG
    private long _debugLastHeldBackTick = -1;
#endif

    public void Run(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (!cancellationToken.IsCancellationRequested)
            this.RunOneIteration(cancellationToken);
    }

    /// <summary>
    /// Single frame of the consumption loop.
    ///
    /// Housekeeping first: finalise any deferred tasks that completed since last frame (release pins, re-schedule). Then acquire
    /// all entries the producer has published since the last advance. If at least two entries are available (the held-back previous
    /// plus one or more new), dispatch due deferred consumers, hand the segment to the fast consumer, and advance past all but the
    /// last entry. Finally, sleep for any remaining frame budget.
    /// </summary>
    private void RunOneIteration(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frameStart = _clock.NowNanoseconds;

        _deferred.EnsureScheduleInitialized(frameStart);
        _deferred.DrainCompletedTasks(frameStart);

        var segment = _shared.Queue.ConsumerAcquire();
        if (segment.Count >= 2)
        {
            this.AssertSegmentTickInvariants(in segment);

            var latestEntry = segment[^1];

            // Clamp: if the production thread published between our clock read and the acquire, frameStart could precede the
            // entry's publish time. Clamping to zero-elapsed prevents negative interpolation values.
            frameStart = Math.Max(frameStart, latestEntry.PublishTimeNanoseconds);

            var latestPosition = segment.StartPosition + segment.Count - 1;
            _deferred.MaybeRunConsumers(in latestEntry, latestPosition, frameStart, cancellationToken);

            _consumer.Consume(in segment, frameStart, cancellationToken);

            this.UpdateDebugLastHeldBackTick(segment[^1].Tick);

            // Advance past all but the last entry. The last entry stays in the queue as the "previous" for next frame's
            // interpolation range.
            _shared.Queue.ConsumerAdvance(segment.Count - 1);
        }

        this.ThrottleToFrameRate(frameStart, cancellationToken);
    }

    // ── Debug invariants ───────────────────────────────────────────────────────

    [Conditional("DEBUG")]
    private void AssertSegmentTickInvariants(
        in Core.Collections.ProducerConsumerQueue<PipelineEntry<TPayload>>.Segment segment)
    {
#if DEBUG
        if (_debugLastHeldBackTick >= 0)
            Debug.Assert(segment[0].Tick == _debugLastHeldBackTick, $"First entry tick ({segment[0].Tick}) must equal the held-back tick from the previous frame ({_debugLastHeldBackTick}).");

        var previousTick = segment[0].Tick;
        for (var i = 1; i < (int)segment.Count; i++)
        {
            var tick = segment[i].Tick;
            Debug.Assert(tick == previousTick + 1, $"Segment ticks must be contiguous: expected {previousTick + 1} at index {i}, got {tick}.");
            previousTick = tick;
        }
#endif
    }

    [Conditional("DEBUG")]
    private void UpdateDebugLastHeldBackTick(long tick)
    {
#if DEBUG
        _debugLastHeldBackTick = tick;
#endif
    }

    // ── Frame throttle ───────────────────────────────────────────────────────

    private void ThrottleToFrameRate(long frameStart, CancellationToken cancellationToken)
    {
        var elapsed = Math.Max(0, _clock.NowNanoseconds - frameStart);
        var remaining = _config.RenderIntervalNanoseconds - elapsed;
        if (remaining > 0)
            _waiter.Wait(new TimeSpan(remaining / 100), cancellationToken);
    }
}
