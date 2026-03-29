using System.Diagnostics;
using Simulation.Memory;
using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// The simulation thread: advances world state one tick at a time and reclaims
/// snapshots that all consumers have moved past.
///
/// Responsibilities:
///   1. Tick loop with accumulator (wall-clock agnostic, deterministic tick count)
///   2. Backpressure: logarithmic delay before each tick when pool is under pressure
///   3. Cleanup pass: free snapshots older than min(rendererEpoch, minPinnedVersion)
/// </summary>
internal sealed class SimulationLoop
{
    private readonly MemorySystem _memory;
    private readonly SharedState  _shared;

    private int _currentTick;
    private WorldSnapshot? _current;  // tail of the live chain
    private WorldSnapshot? _oldest;   // head of the live chain (oldest still needed)

    public SimulationLoop(MemorySystem memory, SharedState shared)
    {
        _memory = memory;
        _shared = shared;
    }

    public void Run(CancellationToken ct)
    {
        Bootstrap();

        var clock = Stopwatch.StartNew();
        double accumulator = 0;

        while (!ct.IsCancellationRequested)
        {
            double deltaMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            accumulator += deltaMs;

            while (accumulator >= SimConstants.TickDurationMs)
            {
                ApplyPressureDelay();
                Tick();
                CleanupStaleSnapshots();
                accumulator -= SimConstants.TickDurationMs;
            }

            // Avoid busy-spinning while waiting for the next tick window.
            Thread.Sleep(1);
        }
    }

    // ── Initialisation ───────────────────────────────────────────────────────

    private void Bootstrap()
    {
        var image    = _memory.RentImage()    ?? throw new InvalidOperationException("Image pool empty at startup");
        var snapshot = _memory.RentSnapshot() ?? throw new InvalidOperationException("Snapshot pool empty at startup");

        image.TickNumber = 0;
        snapshot.Initialize(image);

        _current = snapshot;
        _oldest  = snapshot;
        _currentTick = 0;

        _shared.LatestSnapshot = snapshot;
    }

    // ── Tick ─────────────────────────────────────────────────────────────────

    private void Tick()
    {
        _currentTick++;

        // Rent objects, retrying if pool is momentarily exhausted.
        // CleanupStaleSnapshots() is called first each retry to free what can be freed.
        WorldImage?    image    = null;
        WorldSnapshot? snapshot = null;

        while (snapshot is null)
        {
            image    ??= _memory.RentImage();
            snapshot  = image is not null ? _memory.RentSnapshot() : null;

            if (snapshot is null)
            {
                if (image is not null) { _memory.ReturnImage(image); image = null; }
                CleanupStaleSnapshots();
                Thread.Sleep(SimConstants.PoolEmptyRetryMs);
            }
        }

        image!.TickNumber = _currentTick;
        // TODO: advance world state into image

        snapshot.Initialize(image);

        _current!.SetNext(snapshot);
        _current = snapshot;

        _shared.LatestSnapshot = snapshot;
    }

    // ── Cleanup ──────────────────────────────────────────────────────────────

    private void CleanupStaleSnapshots()
    {
        int minAlive = ComputeMinAlive();

        while (_oldest is not null
               && _oldest != _current
               && _oldest.Image.TickNumber < minAlive)
        {
            var toFree = _oldest;
            var image  = toFree.Image;  // capture before Release() nulls it
            var next   = toFree.Next;

            toFree.Release();
            Debug.Assert(toFree.IsUnreferenced, "Snapshot still referenced after cleanup — ref count mismatch");

            _memory.ReturnSnapshot(toFree);
            _memory.ReturnImage(image);

            _oldest = next;
        }
    }

    private int ComputeMinAlive()
    {
        int rendererEpoch = _shared.RendererEpoch;          // volatile read
        int minPinned     = _memory.PinnedVersions.MinPinned;
        return Math.Min(rendererEpoch, minPinned);
    }

    // ── Backpressure ─────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a delay before each tick proportional to pool pressure.
    /// Formula: extraDelay = base * log2(highWater / remaining), capped at max.
    /// This slows wall-clock tick rate smoothly without skipping ticks.
    /// </summary>
    private void ApplyPressureDelay()
    {
        double remaining = 1.0 - _memory.Pressure;

        if (remaining >= SimConstants.PressureHighWater)
            return;

        double delayMs = remaining > 0
            ? Math.Min(SimConstants.PressureMaxDelayMs,
                       SimConstants.PressureBaseDelayMs * Math.Log2(SimConstants.PressureHighWater / remaining))
            : SimConstants.PressureMaxDelayMs;

        Thread.Sleep((int)Math.Ceiling(delayMs));
    }
}
