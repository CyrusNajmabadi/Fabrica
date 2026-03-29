using System.Diagnostics;
using System.Numerics;
using Simulation.Memory;
using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// The simulation thread: advances world state one tick at a time and reclaims
/// snapshots that all consumers have moved past.
///
/// Generic on <typeparamref name="TClock"/> (constrained to struct) so that clock
/// calls are devirtualised and inlined by the JIT/AOT — no interface dispatch.
/// </summary>
internal sealed class SimulationLoop<TClock> where TClock : struct, IClock
{
    private readonly MemorySystem _memory;
    private readonly SharedState  _shared;
    private readonly TClock       _clock;

    private int            _currentTick;
    private WorldSnapshot? _currentSnapshot;
    private WorldSnapshot? _oldestSnapshot;

    // Snapshots extracted from the live chain because they are pinned (e.g. for saving).
    // HashSet gives O(1) add, O(1) remove-per-item, and prevents accidental duplicates.
    private readonly HashSet<WorldSnapshot> _pinnedQueue = new();

    public SimulationLoop(MemorySystem memory, SharedState shared, TClock clock)
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

            // Brief yield while waiting for the next tick window.
            // TODO: replace Thread.Sleep with an injected IDelayService for testability.
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
                // TODO: replace with IDelayService
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
        int consumptionEpoch = _shared.ConsumptionEpoch; // volatile read

        // Walk the chain, freeing every snapshot the consumption side has moved past.
        // Pinned snapshots are severed from the chain and parked in _pinnedQueue so
        // the snapshots after them can still be freed — avoiding the old behaviour of
        // keeping the entire tail alive whenever a single snapshot is pinned.
        while (_oldestSnapshot is not null
               && _oldestSnapshot != _currentSnapshot
               && _oldestSnapshot.Image.TickNumber < consumptionEpoch)
        {
            WorldSnapshot toProcess = _oldestSnapshot;
            _oldestSnapshot = toProcess.Next;

            if (_memory.PinnedVersions.IsPinned(toProcess.Image.TickNumber))
            {
                toProcess.ClearNext();
                _pinnedQueue.Add(toProcess);
            }
            else
            {
                FreeSnapshot(toProcess);
            }
        }

        // Drain any pinned snapshots whose pins have since been released.
        _pinnedQueue.RemoveWhere(snapshot =>
        {
            if (_memory.PinnedVersions.IsPinned(snapshot.Image.TickNumber))
                return false;
            FreeSnapshot(snapshot);
            return true;
        });
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

    private void ApplyPressureDelay()
    {
        long delay = SimulationPressure.ComputeDelay(
            _memory.SnapshotsAvailable,
            _memory.SnapshotPoolCapacity,
            SimulationConstants.PressureBaseDelayNanoseconds,
            SimulationConstants.PressureMaxDelayNanoseconds);

        if (delay > 0)
            // TODO: replace with IDelayService
            Thread.Sleep(new TimeSpan(delay / 100));
    }
}

// ── Pressure math (extracted for independent testability) ────────────────────

/// <summary>
/// Pure pressure-delay calculations for the simulation loop.
/// No dependencies — safe to unit-test in isolation.
/// </summary>
internal static class SimulationPressure
{
    /// <summary>
    /// Returns the nanosecond delay to insert before a tick given current pool pressure.
    /// Returns 0 when available slots are at or above the high-water mark (50% of capacity).
    /// Each halving of available slots doubles the delay (binary exponential), capped at max.
    /// </summary>
    internal static long ComputeDelay(
        int  available,
        int  capacity,
        long baseNanoseconds,
        long maxNanoseconds)
    {
        int threshold = capacity / 2;
        if (available >= threshold) return 0;

        // level = how many times available has halved below threshold
        int level = BitOperations.Log2((uint)threshold)
                  - BitOperations.Log2((uint)Math.Max(1, available));

        if (level >= 63) return maxNanoseconds;

        long delay = baseNanoseconds << level;
        return delay > maxNanoseconds ? maxNanoseconds : delay;
    }
}
