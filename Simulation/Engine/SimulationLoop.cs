using System.Diagnostics;
using System.Numerics;
using Simulation.Memory;
using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// The simulation thread: advances world state one tick at a time and reclaims
/// snapshots that all consumers have moved past.
///
/// Responsibilities:
///   1. Tick loop with nanosecond accumulator (wall-clock agnostic, deterministic tick count)
///   2. Backpressure: logarithmic delay before each tick when pool is under pressure
///   3. Cleanup pass: free snapshots the renderer epoch has passed, excepting pinned ones
/// </summary>
internal sealed class SimulationLoop
{
    private readonly MemorySystem _memory;
    private readonly SharedState  _shared;
    private readonly IClock       _clock;

    private int            _currentTick;
    private WorldSnapshot? _currentSnapshot;
    private WorldSnapshot? _oldestSnapshot;

    // Snapshots extracted from the live chain because they are pinned (e.g. for saving).
    // Held here until the pin is released, then freed on the next cleanup pass.
    private readonly List<WorldSnapshot> _pinnedQueue = new();

    public SimulationLoop(MemorySystem memory, SharedState shared, IClock clock)
    {
        _memory = memory;
        _shared = shared;
        _clock  = clock;
    }

    public void Run(CancellationToken cancellationToken)
    {
        Bootstrap();

        long lastTime    = _clock.NowNanoseconds;
        long accumulator = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            long now   = _clock.NowNanoseconds;
            long delta = now - lastTime;
            lastTime   = now;
            accumulator += delta;

            while (accumulator >= SimulationConstants.TickDurationNanoseconds)
            {
                ApplyPressureDelay();
                Tick();
                CleanupStaleSnapshots();
                accumulator -= SimulationConstants.TickDurationNanoseconds;
            }

            // Yield briefly rather than busy-spinning while waiting for the next tick window.
            Thread.Sleep(new TimeSpan(SimulationConstants.PoolEmptyRetryNanoseconds / 100));
        }
    }

    // ── Initialisation ───────────────────────────────────────────────────────

    private void Bootstrap()
    {
        WorldImage    image    = _memory.RentImage()    ?? throw new InvalidOperationException("Image pool empty at startup.");
        WorldSnapshot snapshot = _memory.RentSnapshot() ?? throw new InvalidOperationException("Snapshot pool empty at startup.");

        image.TickNumber = 0;
        snapshot.Initialize(image);

        _currentSnapshot = snapshot;
        _oldestSnapshot  = snapshot;
        _currentTick     = 0;

        _shared.LatestSnapshot = snapshot;
    }

    // ── Tick ─────────────────────────────────────────────────────────────────

    private void Tick()
    {
        _currentTick++;

        // Rent from pools, retrying if momentarily exhausted.
        // Cleanup is attempted each retry to free whatever the consumers have released.
        WorldImage?    image    = null;
        WorldSnapshot? snapshot = null;

        while (snapshot is null)
        {
            image    ??= _memory.RentImage();
            snapshot   = image is not null ? _memory.RentSnapshot() : null;

            if (snapshot is null)
            {
                if (image is not null) { _memory.ReturnImage(image); image = null; }
                CleanupStaleSnapshots();
                Thread.Sleep(new TimeSpan(SimulationConstants.PoolEmptyRetryNanoseconds / 100));
            }
        }

        image!.TickNumber = _currentTick;
        // TODO: advance world state into image

        snapshot.Initialize(image);
        _currentSnapshot!.SetNext(snapshot);
        _currentSnapshot = snapshot;

        _shared.LatestSnapshot = snapshot;
    }

    // ── Cleanup ──────────────────────────────────────────────────────────────

    private void CleanupStaleSnapshots()
    {
        int rendererEpoch = _shared.RendererEpoch; // volatile read

        // Walk the chain forward, freeing every snapshot the renderer has moved past.
        // When a pinned snapshot is encountered, sever its Next pointer and park it in
        // _pinnedQueue — this lets the snapshots after it be freed normally, rather than
        // keeping the entire tail of the chain alive just because one snapshot is pinned.
        while (_oldestSnapshot is not null
               && _oldestSnapshot != _currentSnapshot
               && _oldestSnapshot.Image.TickNumber < rendererEpoch)
        {
            WorldSnapshot toProcess = _oldestSnapshot;
            _oldestSnapshot = toProcess.Next;

            if (_memory.PinnedVersions.IsPinned(toProcess.Image.TickNumber))
            {
                toProcess.ClearNext(); // decouple from rest of chain
                _pinnedQueue.Add(toProcess);
            }
            else
            {
                FreeSnapshot(toProcess);
            }
        }

        // Drain pinned snapshots whose pins have since been released.
        for (int index = _pinnedQueue.Count - 1; index >= 0; index--)
        {
            WorldSnapshot snapshot = _pinnedQueue[index];
            if (!_memory.PinnedVersions.IsPinned(snapshot.Image.TickNumber))
            {
                _pinnedQueue.RemoveAt(index);
                FreeSnapshot(snapshot);
            }
        }
    }

    private void FreeSnapshot(WorldSnapshot snapshot)
    {
        WorldImage image = snapshot.Image; // capture before Release() nulls it
        snapshot.Release();
        Debug.Assert(snapshot.IsUnreferenced, "Snapshot still referenced after cleanup — refcount mismatch.");
        _memory.ReturnSnapshot(snapshot);
        _memory.ReturnImage(image);
    }

    // ── Backpressure ─────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a delay before each tick proportional to pool pressure using
    /// integer bit-arithmetic to approximate log₂ without floating-point.
    /// Each halving of available slots doubles the delay, capped at the maximum.
    /// Slows wall-clock tick rate without skipping or duplicating ticks.
    /// </summary>
    private void ApplyPressureDelay()
    {
        int available = _memory.SnapshotsAvailable;
        int threshold = _memory.SnapshotPoolCapacity / 2; // high-water mark: 50 %

        if (available >= threshold) return;

        // level = floor(log2(threshold)) - floor(log2(max(1, available)))
        // Each unit of level represents one additional halving of available slots.
        int level = BitOperations.Log2((uint)threshold)
                  - BitOperations.Log2((uint)Math.Max(1, available));

        long delayNanoseconds = SimulationConstants.PressureBaseDelayNanoseconds << level;
        if (delayNanoseconds > SimulationConstants.PressureMaxDelayNanoseconds || level >= 63)
            delayNanoseconds = SimulationConstants.PressureMaxDelayNanoseconds;

        Thread.Sleep(new TimeSpan(delayNanoseconds / 100));
    }
}
