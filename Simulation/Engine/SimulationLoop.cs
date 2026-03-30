using System.Diagnostics;
using Simulation.Memory;
using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// The simulation ("producer") thread.  Advances world state one tick at a time,
/// maintains the snapshot chain, and reclaims snapshots the consumption thread
/// has finished with.
///
/// SNAPSHOT CHAIN
///   Every tick appends a new WorldSnapshot to a singly-linked forward chain:
///
///     _oldestSnapshot → [tick 1] → [tick 2] → … → [tick N] ← _currentSnapshot
///                                                             (= LatestSnapshot)
///
///   The simulation is the sole writer to the chain.  WorldImage data is written
///   before the volatile-write to LatestSnapshot (a release fence), so the
///   consumption thread always sees fully-initialised image data (acquire fence
///   on the matching read).
///
/// EPOCH-BASED RECLAMATION
///   After each tick, CleanupStaleSnapshots() reads ConsumptionEpoch (volatile
///   acquire) and walks _oldestSnapshot forward, freeing every snapshot whose
///   tick is strictly less than the epoch and is not pinned for saving.
///
///   Safety argument: ConsumptionEpoch = N means the consumption thread has
///   successfully completed Render(…, tickN) and set _lastRendered = tickN.
///   The simulation reads this volatile value; if it sees a stale (lower) epoch
///   it merely retains a snapshot one extra cleanup pass — never frees prematurely.
///
/// PINNED SNAPSHOTS AND THE PINNED QUEUE
///   A snapshot being saved must not be reclaimed while the save task reads it.
///   When CleanupStaleSnapshots encounters a pinned snapshot it cannot free, it
///   calls ClearNext() to sever the snapshot from the live chain and parks it in
///   _pinnedQueue.  This lets _oldestSnapshot advance past the pinned node so
///   that everything after it can still be freed normally.
///   The pinned queue is drained each cleanup pass once the pin is released.
///
/// BACKPRESSURE
///   When the snapshot pool drops below roughly half capacity, ApplyPressureDelay()
///   inserts an exponentially growing delay before each tick.  This slows
///   the simulation, giving the consumption thread time to advance its epoch and
///   allow cleanup to reclaim more snapshots.  If the pool is fully exhausted,
///   Tick() retries with a 1 ms sleep per attempt.
///
/// Generic on <typeparamref name="TClock"/> and <typeparamref name="TWaiter"/>
/// (both constrained to struct) so the JIT/AOT devirtualises all calls —
/// zero interface-dispatch overhead in the hot path.
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
        cancellationToken.ThrowIfCancellationRequested();

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
        long delta = Math.Max(0, now - lastTime);
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

    /// <summary>
    /// Allocates the tick-0 snapshot, anchors both _oldestSnapshot and
    /// _currentSnapshot to it, and publishes it via LatestSnapshot so the
    /// consumption thread has something to render before the first tick completes.
    /// </summary>
    private void Bootstrap()
    {
        WorldImage    image    = _memory.RentImage()    ?? throw new InvalidOperationException("Image pool empty at startup.");
        WorldSnapshot? snapshot = _memory.RentSnapshot();

        if (snapshot is null)
        {
            _memory.ReturnImage(image);
            throw new InvalidOperationException("Snapshot pool empty at startup.");
        }

        snapshot.Initialize(image, tickNumber: 0);

        _currentSnapshot = snapshot;
        _oldestSnapshot  = snapshot;
        _currentTick     = 0;

        snapshot.MarkPublished(_clock.NowNanoseconds);
        _shared.LatestSnapshot = snapshot;
    }

    // ── Tick ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Advances the world by one tick.
    ///
    /// Rents a fresh image and snapshot, writes the new world state, links the
    /// snapshot onto the chain via SetNext, and volatile-writes it to
    /// LatestSnapshot (release fence — makes all image writes visible to the
    /// consumption thread's subsequent acquire-read).
    ///
    /// Pool retry: if either object is unavailable, CleanupStaleSnapshots() is
    /// called immediately to reclaim what it can, then the thread sleeps 1 ms
    /// before retrying.  This tight loop is the last resort — backpressure
    /// (ApplyPressureDelay) should slow the tick rate long before this path
    /// is reached under normal conditions.
    /// </summary>
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
        // TODO: advance world state into image

        snapshot.Initialize(image!, _currentTick);
        _currentSnapshot!.SetNext(snapshot);
        _currentSnapshot = snapshot;

        snapshot.MarkPublished(_clock.NowNanoseconds);
        _shared.LatestSnapshot = snapshot;
    }

    // ── Cleanup ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reclaims snapshots the consumption thread has moved past.
    ///
    /// Pass 1 — live chain walk:
    ///   Reads ConsumptionEpoch once (volatile acquire).  Walks _oldestSnapshot
    ///   forward while tick &lt; epoch and node != _currentSnapshot.  Each node:
    ///     - Not pinned → FreeSnapshot immediately.
    ///     - Pinned → ClearNext (severs from chain) then park in _pinnedQueue.
    ///   Severing a pinned node lets _oldestSnapshot advance past it so the
    ///   snapshots after it are not blocked from reclamation.
    ///
    /// Pass 2 — pinned queue drain:
    ///   Scans _pinnedQueue and frees any snapshot whose pin has since cleared.
    ///
    /// Invariant: _currentSnapshot is never freed here.  The loop guard
    /// (_oldestSnapshot != _currentSnapshot) always leaves the latest published
    /// snapshot alive regardless of the epoch value.
    /// </summary>
    private void CleanupStaleSnapshots()
    {
        int consumptionEpoch = _shared.ConsumptionEpoch; // volatile read

        // Walk the chain, freeing every snapshot the consumption side has moved past.
        // Pinned snapshots are severed from the chain and parked in _pinnedQueue so
        // the snapshots after them can still be freed — avoiding the old behaviour of
        // keeping the entire tail alive whenever a single snapshot is pinned.
        while (_oldestSnapshot is not null
               && _oldestSnapshot != _currentSnapshot
               && _oldestSnapshot.TickNumber < consumptionEpoch)
        {
            WorldSnapshot toProcess = _oldestSnapshot;
            _oldestSnapshot = toProcess.Next;

            if (_memory.PinnedVersions.IsPinned(toProcess.TickNumber))
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
            if (_memory.PinnedVersions.IsPinned(snapshot.TickNumber))
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

    /// <summary>
    /// Inserts a delay before each tick proportional to pool pressure.
    /// No delay when the pool is lightly used; up to 64 ms when nearly full.
    /// See <see cref="SimulationPressure.ComputeDelay"/> for the bucket schedule.
    /// </summary>
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

        public void RunOneIteration(
            CancellationToken cancellationToken,
            ref long lastTime,
            ref long accumulator) =>
            _loop.RunOneIteration(cancellationToken, ref lastTime, ref accumulator);

        public int CurrentTick => _loop._currentTick;

        public WorldSnapshot? CurrentSnapshot => _loop._currentSnapshot;

        public WorldSnapshot? OldestSnapshot => _loop._oldestSnapshot;

        public int PinnedQueueCount => _loop._pinnedQueue.Count;

        public void SetOldestSnapshotForTesting(WorldSnapshot snapshot) => _loop._oldestSnapshot = snapshot;
    }
}

// ── Pressure math (extracted for independent testability) ────────────────────

/// <summary>
/// Pure pressure-delay calculations for the simulation loop.
/// No dependencies — extracted so it can be unit-tested in isolation.
/// </summary>
internal static class SimulationPressure
{
    /// <summary>
    /// Returns the nanosecond delay to insert before a tick given current pool pressure.
    ///
    /// The pool capacity is divided into 8 equal buckets by usage fraction.
    /// Bucket 0 (pool mostly empty = light use) incurs no delay.  Each subsequent
    /// bucket doubles the previous delay (binary-exponential):
    ///
    ///   Bucket:  0    1    2    3    4     5     6     7
    ///   Delay:   0ms  1ms  2ms  4ms  8ms  16ms  32ms  64ms
    ///
    /// Binary-exponential was chosen because:
    ///   • It is computed with a single integer bit-shift — no floating-point.
    ///   • It responds sharply when the pool is nearly exhausted, giving the
    ///     consumption thread time to advance its epoch before the simulation
    ///     fills the pool completely.
    ///   • Delay is capped at maxNanoseconds so the simulation never stalls
    ///     indefinitely due to backpressure alone.
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
