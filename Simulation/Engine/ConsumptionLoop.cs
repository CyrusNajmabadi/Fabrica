using System.Collections.Concurrent;
using Simulation.Memory;
using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// The consumption ("consumer") thread.  Renders the latest snapshot at ≈60 fps
/// and coordinates periodic saves.
///
/// FRAME LOOP  (RunOneIteration)
///   1. Volatile-read LatestSnapshot (acquire fence: all image writes by the
///      simulation thread are now visible).
///   2. MaybeStartSave: if a save is due and not in flight, pin the snapshot
///      and dispatch to the save task.  The epoch has NOT yet advanced past
///      this tick, so the simulation already holds the snapshot alive.
///   3. Render(_lastRendered, snapshot): always fires — see RENDER SEMANTICS.
///   4. ConsumptionEpoch = snapshot.TickNumber (volatile release fence).
///   5. _lastRendered = snapshot.
///   6. ThrottleToFrameRate: sleep for any remaining frame budget (≈16.67 ms).
///
/// RENDER SEMANTICS
///   Render is called every frame regardless of whether the simulation has
///   produced a new snapshot since the last call.  When the simulation is
///   running behind the render rate, LatestSnapshot returns the same object on
///   multiple consecutive frames, so previous == current (same reference).
///   The renderer may use this to drive sub-tick animation or interpolation, or
///   detect the no-change case cheaply via ReferenceEquals(previous, current).
///   See IRenderer.Render for full details.
///
/// SAVE PIN DISCIPLINE
///   The ordering within a save-triggering frame is load-bearing:
///
///     1. Pin(tick)         ← epoch hasn't advanced; simulation holds snapshot alive
///     2. RunSave(image)    ← dispatch to save task
///     3. Render(…)         ← may throw; epoch still not advanced (safe)
///     4. ConsumptionEpoch = tick  ← simulation may now look past this tick
///
///   If RunSave throws (dispatch failure): pin is released, NextSaveAtTick is
///   restored to the current tick, and the save will be retried next frame.
///   If Render throws after a successful dispatch: the pin remains so the save
///   task can still safely read the image.  The save task's finally block always
///   clears the pin and reschedules NextSaveAtTick.
///
/// EPOCH ORDERING GUARANTEE
///   ConsumptionEpoch = N implies Render(…, tickN) has already returned
///   successfully and _lastRendered == tickN.  The simulation can therefore
///   safely reclaim tickN's snapshot once epoch advances past it.
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

    // The snapshot passed as 'current' on the most recent successful Render call.
    // Null before the first render.
    // Passed as 'previous' on the next Render call so the renderer can diff the two.
    // Only touched by the consumption thread — no synchronisation needed.
    private WorldSnapshot? _lastRendered;

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
        long frameStart = _clock.NowNanoseconds;

        DrainSaveEvents();

        WorldSnapshot? snapshot = _shared.LatestSnapshot;
        if (snapshot is not null)
        {
            MaybeStartSave(snapshot);

            var frame = new RenderFrame
            {
                Previous = _lastRendered,
                Current = snapshot,
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

            // Advance epoch: simulation may now free anything strictly before this tick.
            _shared.ConsumptionEpoch = snapshot.Image.TickNumber;
            _lastRendered = snapshot;
        }

        ThrottleToFrameRate(frameStart, cancellationToken);
    }

    private void DrainSaveEvents()
    {
        while (_saveEvents.TryDequeue(out SaveEvent saveEvent))
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
        int nextSaveAt = _shared.NextSaveAtTick;

        if (nextSaveAt == 0 || snapshot.Image.TickNumber < nextSaveAt)
            return;

        int tickToSave = snapshot.Image.TickNumber;

        // Clear the timer BEFORE firing so the save task controls the next trigger.
        _shared.NextSaveAtTick = 0;

        // Pin the snapshot so the simulation will not reclaim it while saving.
        // Safe: ConsumptionEpoch has not yet advanced past tickToSave (we advance it
        // after Render below), so the simulation already holds this snapshot alive.
        _memory.PinnedVersions.Pin(tickToSave, _savePinOwner);

        WorldImage imageToSave = snapshot.Image;
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
        try
        {
            _saver.Save(image, tick);
            _saveEvents.Enqueue(new SaveEvent(tick, Succeeded: true, Error: null));
        }
        catch (Exception ex)
        {
            _saveEvents.Enqueue(new SaveEvent(tick, Succeeded: false, Error: ex));
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
        long elapsed = Math.Max(0, _clock.NowNanoseconds - frameStart);
        long remaining = SimulationConstants.RenderIntervalNanoseconds - elapsed;
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
