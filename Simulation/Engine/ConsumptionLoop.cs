using System.Diagnostics;
using Simulation.Memory;
using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// The consumption thread: renders the latest snapshot at ~60 fps and periodically
/// triggers a save task.
///
/// Epoch discipline:
///   - RendererEpoch is advanced AFTER rendering is complete.
///   - The save pin is set BEFORE handing off to the save task (while epoch still covers it).
///   - The save task clears the pin only after it has finished reading the snapshot.
/// </summary>
internal sealed class ConsumptionLoop
{
    private readonly SharedState  _shared;
    private readonly MemorySystem _memory;

    public ConsumptionLoop(SharedState shared, MemorySystem memory)
    {
        _shared = shared;
        _memory = memory;
    }

    public void Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frameStart = Stopwatch.GetTimestamp();

            var snapshot = _shared.LatestSnapshot;
            if (snapshot is not null)
            {
                MaybeStartSave(snapshot);
                Render(snapshot);

                // Epoch advance: simulation may now free anything strictly before this tick.
                _shared.RendererEpoch = snapshot.Image.TickNumber;
            }

            ThrottleToFrameRate(frameStart);
        }
    }

    // ── Save coordination ────────────────────────────────────────────────────

    private void MaybeStartSave(WorldSnapshot snapshot)
    {
        int nextSaveAt = _shared.NextSaveAtTick;

        // 0 = no save scheduled.  Otherwise, wait until simulation has reached that tick.
        if (nextSaveAt == 0 || snapshot.Image.TickNumber < nextSaveAt)
            return;

        int tickToSave = snapshot.Image.TickNumber;

        // Clear the timer BEFORE firing so the save task controls the next trigger.
        _shared.NextSaveAtTick = 0;

        // Pin the snapshot so the simulation won't reclaim it while saving.
        // Safe: our RendererEpoch hasn't advanced past tickToSave yet, so the
        // simulation is already holding it alive.
        _memory.PinnedVersions.Pin(tickToSave);

        // Hand off to a background task.  We capture only what the task needs.
        Task.Run(() => RunSaveTask(snapshot.Image, tickToSave));
    }

    private void RunSaveTask(WorldImage image, int tick)
    {
        try
        {
            Save(image, tick);
        }
        finally
        {
            // Always unpin, even if save throws — prevents the simulation from
            // being blocked forever by a failed save.
            _memory.PinnedVersions.Unpin(tick);

            // Schedule the next save relative to the tick we just saved.
            _shared.NextSaveAtTick = tick + SimConstants.SaveIntervalTicks;
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

    private static void ThrottleToFrameRate(long frameStart)
    {
        double elapsedMs = (Stopwatch.GetTimestamp() - frameStart) * 1000.0 / Stopwatch.Frequency;
        int remainingMs  = SimConstants.RenderIntervalMs - (int)elapsedMs;
        if (remainingMs > 0)
            Thread.Sleep(remainingMs);
    }
}
