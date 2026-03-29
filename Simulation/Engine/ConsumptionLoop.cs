using Simulation.Memory;
using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// The consumption thread: renders the latest snapshot at ~60 fps and periodically
/// triggers a save task.
///
/// Generic on <typeparamref name="TClock"/> (constrained to struct) so that clock
/// calls are devirtualised and inlined by the JIT/AOT — no interface dispatch.
/// Generic on <typeparamref name="TWaiter"/> for the same reason.
/// Generic on <typeparamref name="TSaveRunner"/> for the same reason.
///
/// Epoch discipline:
///   - ConsumptionEpoch is advanced only AFTER rendering is complete.
///   - The save pin is set BEFORE handing off to the save task, while the epoch
///     still covers that snapshot — the simulation cannot reclaim it.
///   - The save task clears the pin only after it has finished reading the snapshot.
/// </summary>
internal sealed class ConsumptionLoop<TClock, TWaiter, TSaveRunner>
    where TClock : struct, IClock
    where TWaiter : struct, IWaiter
    where TSaveRunner : struct, ISaveRunner
{
    private readonly MemorySystem _memory;
    private readonly SharedState  _shared;
    private readonly TClock       _clock;
    private readonly TWaiter      _waiter;
    private readonly TSaveRunner  _saveRunner;

    public ConsumptionLoop(
        MemorySystem memory,
        SharedState shared,
        TClock clock,
        TWaiter waiter,
        TSaveRunner saveRunner)
    {
        _memory = memory;
        _shared = shared;
        _clock  = clock;
        _waiter = waiter;
        _saveRunner = saveRunner;
    }

    public void Run(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            long frameStart = _clock.NowNanoseconds;

            WorldSnapshot? snapshot = _shared.LatestSnapshot;
            if (snapshot is not null)
            {
                MaybeStartSave(snapshot);
                Render(snapshot);

                // Advance epoch: simulation may now free anything strictly before this tick.
                _shared.ConsumptionEpoch = snapshot.Image.TickNumber;
            }

            ThrottleToFrameRate(frameStart, cancellationToken);
        }
    }

    // ── Save coordination ────────────────────────────────────────────────────

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
        _memory.PinnedVersions.Pin(tickToSave);

        WorldImage imageToSave = snapshot.Image;
        _saveRunner.RunSave(imageToSave, tickToSave, RunSaveTask);
    }

    private void RunSaveTask(WorldImage image, int tick)
    {
        try
        {
            Save(image, tick);
        }
        finally
        {
            // Always unpin — even on failure — so the simulation is never permanently stalled.
            _memory.PinnedVersions.Unpin(tick);

            // Schedule the next save relative to the tick we just saved.
            _shared.NextSaveAtTick = tick + SimulationConstants.SaveIntervalTicks;
        }
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    private static void Render(WorldSnapshot snapshot)
    {
        // TODO: actual rendering — interpolation between snapshot N and N+1 goes here.
        Console.WriteLine($"[Render] tick={snapshot.Image.TickNumber}");
    }

    private static void Save(WorldImage image, int tick)
    {
        // TODO: actual serialisation.
        Console.WriteLine($"[Save]   tick={tick} — saving...");
        Thread.Sleep(1000); // placeholder for real I/O
        Console.WriteLine($"[Save]   tick={tick} — done.");
    }

    // ── Frame timing ─────────────────────────────────────────────────────────

    private void ThrottleToFrameRate(long frameStart, CancellationToken cancellationToken)
    {
        long elapsed   = _clock.NowNanoseconds - frameStart;
        long remaining = SimulationConstants.RenderIntervalNanoseconds - elapsed;
        if (remaining > 0)
            _waiter.Wait(new TimeSpan(remaining / 100), cancellationToken);
    }
}
