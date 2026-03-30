using System.Collections.Concurrent;
using Simulation.Memory;
using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// The consumption ("consumer") thread.  Renders the latest snapshot at ≈60 fps
/// and coordinates periodic saves.
///
/// FRAME LOOP  (RunOneIteration)
///   1. DrainSaveEvents: process any completed save results from the threadpool.
///   2. Volatile-read LatestSnapshot (acquire fence: all image writes by the
///      simulation thread are now visible).
///   3. Rotate the render pair if a new snapshot has arrived (see below).
///   4. MaybeStartSave: if a save is due and not in flight, pin the snapshot
///      and dispatch to the save task.
///   5. Build RenderFrame (snapshots + interpolation factor + engine status).
///   6. Render(frame): the renderer uses the snapshots for the duration of the
///      call only — it must not store them.
///   7. ConsumptionEpoch = _renderPrevious.TickNumber (or _renderCurrent's if
///      no previous exists yet).  This keeps both held snapshots alive.
///   8. ThrottleToFrameRate: sleep for any remaining frame budget (≈16.67 ms).
///
/// "ONE TICK BEHIND" INTERPOLATION MODEL
///   The loop holds two distinct snapshot references: _renderPrevious and
///   _renderCurrent.  When LatestSnapshot changes (a new tick), the pair
///   rotates: old current becomes previous, new snapshot becomes current.
///   Between rotations the pair is stable.
///
///   The interpolation factor progresses from 0 (show previous) to 1 (show
///   current) over one tick duration, based on wall-clock time elapsed since
///   current was published.  This gives the renderer two real simulation
///   endpoints to blend between — no extrapolation needed.
///
///   Visual latency: ~one tick (25 ms at 40 Hz).  Imperceptible for a factory
///   simulation where events occur on timescales of seconds.
///
/// EPOCH ADVANCEMENT
///   The epoch is set to _renderPrevious.TickNumber when both snapshots exist,
///   or _renderCurrent.TickNumber on the first frame (no previous yet).
///   This advances one tick behind the latest consumed snapshot, keeping both
///   held snapshots alive: previous at tick N is safe (N is not &lt; N), and
///   current at tick &gt; N is safe.  The simulation retains one extra snapshot
///   compared to the old model — negligible with a 256-slot pool.
///
/// SAVE PIN DISCIPLINE
///   The ordering within a save-triggering frame is load-bearing:
///
///     1. Pin(tick)         ← epoch hasn't advanced; simulation holds snapshot alive
///     2. RunSave(image)    ← dispatch to save task
///     3. Render(…)         ← may throw; epoch still not advanced (safe)
///     4. ConsumptionEpoch  ← simulation may now look past older ticks
///
///   If RunSave throws (dispatch failure): pin is released, NextSaveAtTick is
///   restored to the current tick, and the save will be retried next frame.
///   If Render throws after a successful dispatch: the pin remains so the save
///   task can still safely read the image.  The save task's finally block always
///   clears the pin and reschedules NextSaveAtTick.
///
/// Generic constraints (struct IClock, IWaiter, etc.) eliminate interface
/// dispatch on every call in the hot frame loop.  See Engine summary for
/// the full rationale.
/// </summary>
internal sealed class ConsumptionLoop<TClock, TWaiter, TSaveRunner, TSaver, TRenderer>
    where TClock : struct, IClock
    where TWaiter : struct, IWaiter
    where TSaveRunner : struct, ISaveRunner
    where TSaver : struct, ISaver
    where TRenderer : struct, IRenderer
{
    private readonly object _savePinOwner = new();
    private readonly ConcurrentQueue<SaveEvent> _saveEvents = new();

    // The two snapshots the renderer interpolates between.
    // Only touched by the consumption thread — no synchronisation needed.
    // _renderPrevious is null before two distinct snapshots have been observed.
    private WorldSnapshot? _renderPrevious;
    private WorldSnapshot? _renderCurrent;

    // Local save tracking — written only by the consumption thread (after draining
    // the concurrent queue), so no synchronisation needed for reads.
    private bool _saveInFlight;
    private SaveEvent? _lastSaveResult;

    private readonly MemorySystem _memory;
    private readonly SharedState _shared;
    private readonly TClock _clock;
    private readonly TWaiter _waiter;
    private readonly TSaveRunner _saveRunner;
    private readonly TSaver _saver;
    private readonly TRenderer _renderer;

    public ConsumptionLoop(
        MemorySystem memory,
        SharedState shared,
        TClock clock,
        TWaiter waiter,
        TSaveRunner saveRunner,
        TSaver saver,
        TRenderer renderer)
    {
        _memory = memory;
        _shared = shared;
        _clock = clock;
        _waiter = waiter;
        _saveRunner = saveRunner;
        _saver = saver;
        _renderer = renderer;
    }

    public void Run(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (!cancellationToken.IsCancellationRequested)
            RunOneIteration(cancellationToken);
    }

    private void RunOneIteration(CancellationToken cancellationToken)
    {
        var frameStart = _clock.NowNanoseconds;

        DrainSaveEvents();

        var latestSnapshot = _shared.LatestSnapshot;
        if (latestSnapshot is not null)
        {
            if (!ReferenceEquals(latestSnapshot, _renderCurrent))
            {
                _renderPrevious = _renderCurrent;
                _renderCurrent = latestSnapshot;
            }

            MaybeStartSave(_renderCurrent!);

            var frame = new RenderFrame
            {
                Previous = _renderPrevious,
                Current = _renderCurrent!,
                Interpolation = new InterpolationClock
                {
                    ElapsedNanoseconds = frameStart - _renderCurrent!.PublishTimeNanoseconds,
                    TickDurationNanoseconds = SimulationConstants.TickDurationNanoseconds,
                },
                EngineStatus = new EngineStatus
                {
                    Save = new SaveStatus
                    {
                        InFlight = _saveInFlight,
                        LastResult = _lastSaveResult,
                    },
                },
            };

            _renderer.Render(in frame);

            // Epoch protects both held snapshots.  When we have a previous,
            // set epoch to its tick — cleanup frees strictly below, so both
            // previous (tick N, not < N) and current (tick > N) remain alive.
            _shared.ConsumptionEpoch = _renderPrevious?.TickNumber
                                       ?? _renderCurrent!.TickNumber;
        }

        ThrottleToFrameRate(frameStart, cancellationToken);
    }

    private void DrainSaveEvents()
    {
        while (_saveEvents.TryDequeue(out var saveEvent))
        {
            _saveInFlight = false;
            _lastSaveResult = saveEvent;
        }
    }

    /// <summary>
    /// Starts a background save if the latest tick has reached (or passed) the
    /// scheduled save tick and no save is currently in flight.
    ///
    /// "In flight" is signalled by NextSaveAtTick == 0.  This field is cleared
    /// here, before dispatch, so a second frame cannot trigger a double-save
    /// against the same snapshot.  The save task resets it in its finally block.
    ///
    /// Pin ordering: the tick is pinned BEFORE RunSave is called.  At this point
    /// ConsumptionEpoch has not yet advanced past the tick, so the simulation
    /// already holds the snapshot alive via the epoch.  The pin extends that
    /// protection beyond the epoch advance that happens later in the same frame.
    /// On dispatch failure the pin is immediately released and the save tick is
    /// restored so the next frame can retry.
    /// </summary>
    private void MaybeStartSave(WorldSnapshot snapshot)
    {
        var nextSaveAt = _shared.NextSaveAtTick;

        if (nextSaveAt == 0 || snapshot.TickNumber < nextSaveAt)
            return;

        var tickToSave = snapshot.TickNumber;

        // Clear the timer BEFORE firing so the save task controls the next trigger.
        _shared.NextSaveAtTick = 0;

        // Pin the snapshot so the simulation will not reclaim it while saving.
        // Safe: ConsumptionEpoch has not yet advanced past tickToSave (we advance it
        // after Render below), so the simulation already holds this snapshot alive.
        _memory.PinnedVersions.Pin(tickToSave, _savePinOwner);

        var imageToSave = snapshot.Image;
        try
        {
            _saveRunner.RunSave(imageToSave, tickToSave, RunSaveTask);
            _saveInFlight = true;
        }
        catch
        {
            _memory.PinnedVersions.Unpin(tickToSave, _savePinOwner);
            _shared.NextSaveAtTick = tickToSave;
            throw;
        }
    }

    /// <summary>
    /// Executed on the threadpool (via ISaveRunner) to perform the actual save.
    ///
    /// Exceptions from the saver are caught and reported via the save event queue
    /// rather than propagating as unobserved task exceptions.  The consumption
    /// thread drains the queue each frame and surfaces the result through
    /// <see cref="EngineStatus.Save"/> in the <see cref="RenderFrame"/>.
    ///
    /// The finally block runs regardless of success or failure, guaranteeing:
    ///   - The pin is always released so the simulation is never permanently stalled
    ///     waiting for a save that failed or was never completed.
    ///   - NextSaveAtTick is always rescheduled so saves resume after a failure.
    /// </summary>
    private void RunSaveTask(WorldImage image, int tick)
    {
        var startTime = _clock.NowNanoseconds;
        try
        {
            _saver.Save(image, tick);
            _saveEvents.Enqueue(new SaveEvent(tick, DurationNanoseconds: _clock.NowNanoseconds - startTime, Error: null));
        }
        catch (Exception ex)
        {
            _saveEvents.Enqueue(new SaveEvent(tick, DurationNanoseconds: _clock.NowNanoseconds - startTime, Error: ex));
        }
        finally
        {
            // Always unpin — even on failure — so the simulation is never permanently stalled.
            _memory.PinnedVersions.Unpin(tick, _savePinOwner);

            // Schedule the next save relative to the tick we just saved.
            _shared.NextSaveAtTick = tick + SimulationConstants.SaveIntervalTicks;
        }
    }

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
        private readonly ConsumptionLoop<TClock, TWaiter, TSaveRunner, TSaver, TRenderer> _loop;

        public TestAccessor(ConsumptionLoop<TClock, TWaiter, TSaveRunner, TSaver, TRenderer> loop)
        {
            _loop = loop;
        }

        public void RunOneIteration(CancellationToken cancellationToken) => _loop.RunOneIteration(cancellationToken);

        public void MaybeStartSave(WorldSnapshot snapshot) => _loop.MaybeStartSave(snapshot);

        public void RunSaveTask(WorldImage image, int tick) => _loop.RunSaveTask(image, tick);

        public void ThrottleToFrameRate(long frameStart, CancellationToken cancellationToken) =>
            _loop.ThrottleToFrameRate(frameStart, cancellationToken);
    }
}
