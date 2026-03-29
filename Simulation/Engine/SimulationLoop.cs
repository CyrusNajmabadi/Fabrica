using System.Diagnostics;
using Simulation.Memory;
using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// The simulation thread: advances world state one tick at a time and reclaims
/// snapshots that all consumers have moved past.
///
/// Generic on <typeparamref name="TClock"/> (constrained to struct) so that clock
/// calls are devirtualised and inlined by the JIT/AOT — no interface dispatch.
/// Generic on <typeparamref name="TWaiter"/> for the same reason.
/// </summary>
internal sealed class SimulationLoop<TClock, TWaiter>
    where TClock : struct, IClock
    where TWaiter : struct, IWaiter
{
    private readonly MemorySystem _memory;
    private readonly SharedState  _shared;
    private readonly TClock       _clock;
    private readonly TWaiter      _waiter;

    private int            _currentTick;
    private WorldSnapshot? _currentSnapshot;
    private WorldSnapshot? _oldestSnapshot;

    // Snapshots extracted from the live chain because they are pinned (e.g. for saving).
    // HashSet gives O(1) add, O(1) remove-per-item, and prevents accidental duplicates.
    private readonly HashSet<WorldSnapshot> _pinnedQueue = new();

    public SimulationLoop(MemorySystem memory, SharedState shared, TClock clock, TWaiter waiter)
    {
        _memory = memory;
        _shared = shared;
        _clock  = clock;
        _waiter = waiter;
    }

    public void Run(CancellationToken cancellationToken)
    {
        Bootstrap();

        long lastTime    = _clock.NowNanoseconds;
        long accumulator = 0;

        while (!cancellationToken.IsCancellationRequested)
            RunOneIteration(cancellationToken, ref lastTime, ref accumulator);
    }

    private void RunOneIteration(
        CancellationToken cancellationToken,
        ref long lastTime,
        ref long accumulator)
    {
        long now   = _clock.NowNanoseconds;
        long delta = now - lastTime;
        lastTime   = now;
        accumulator += delta;

        ProcessAvailableTicks(cancellationToken, ref accumulator);

        // Brief yield while waiting for the next tick window.
        _waiter.Wait(new TimeSpan(SimulationConstants.PoolEmptyRetryNanoseconds / 100), cancellationToken);
    }

    private void ProcessAvailableTicks(CancellationToken cancellationToken, ref long accumulator)
    {
        while (accumulator >= SimulationConstants.TickDurationNanoseconds)
        {
            ApplyPressureDelay(cancellationToken);
            Tick(cancellationToken);
            CleanupStaleSnapshots();
            accumulator -= SimulationConstants.TickDurationNanoseconds;
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

    private void Tick(CancellationToken cancellationToken)
    {
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
                _waiter.Wait(new TimeSpan(SimulationConstants.PoolEmptyRetryNanoseconds / 100), cancellationToken);
            }
        }

        _currentTick++;
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
                if (!_pinnedQueue.Add(toProcess))
                    throw new InvalidOperationException("Pinned snapshot was added to the cleanup queue more than once.");
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

    private void ApplyPressureDelay(CancellationToken cancellationToken)
    {
        long delay = SimulationPressure.ComputeDelay(
            _memory.SnapshotsAvailable,
            _memory.SnapshotPoolCapacity,
            SimulationConstants.PressureBaseDelayNanoseconds,
            SimulationConstants.PressureMaxDelayNanoseconds);

        if (delay > 0)
            _waiter.Wait(new TimeSpan(delay / 100), cancellationToken);
    }

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor
    {
        private readonly SimulationLoop<TClock, TWaiter> _loop;

        public TestAccessor(SimulationLoop<TClock, TWaiter> loop)
        {
            _loop = loop;
        }

        public void Bootstrap() => _loop.Bootstrap();

        public void Tick(CancellationToken cancellationToken) => _loop.Tick(cancellationToken);

        public void CleanupStaleSnapshots() => _loop.CleanupStaleSnapshots();

        public int CurrentTick => _loop._currentTick;

        public WorldSnapshot? CurrentSnapshot => _loop._currentSnapshot;

        public WorldSnapshot? OldestSnapshot => _loop._oldestSnapshot;

        public int PinnedQueueCount => _loop._pinnedQueue.Count;
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
    /// Returns the nanosecond delay to insert before a tick given current pool occupancy.
    /// The pool is divided into 8 occupancy buckets with delays:
    ///   0, 1, 2, 4, 8, 16, 32, 64 ms.
    /// </summary>
    internal static long ComputeDelay(
        int  available,
        int  capacity,
        long baseNanoseconds,
        long maxNanoseconds)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        if (available < 0 || available > capacity)
            throw new ArgumentOutOfRangeException(nameof(available));

        int used = capacity - available;
        int bucket = used == 0
            ? 0
            : Math.Min(
                SimulationConstants.PressureBucketCount - 1,
                (used - 1) * SimulationConstants.PressureBucketCount / capacity);

        if (bucket == 0)
            return 0;

        long delay = baseNanoseconds << (bucket - 1);
        return delay > maxNanoseconds ? maxNanoseconds : delay;
    }
}
