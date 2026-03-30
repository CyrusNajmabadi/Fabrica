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
///   The tick-epoch gap (in nanoseconds) drives two pressure levels:
///
///   1. Soft pressure — when the gap exceeds PressureLowWaterMarkNanoseconds,
///      an exponentially increasing delay (1 ms → 64 ms) is inserted before
///      each tick, slowing the simulation and giving the consumption thread
///      time to advance its epoch.
///
///   2. Hard ceiling — when the gap reaches PressureHardCeilingNanoseconds,
///      the simulation blocks entirely, sleeping PressureMaxDelayNanoseconds
///      per iteration until the gap drops below the ceiling.  This bounds
///      memory growth: the simulation cannot run arbitrarily far ahead.
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
    private readonly SharedState _shared;
    private readonly SimulationCoordinator _simulationCoordinator;
    private readonly TClock _clock;
    private readonly TWaiter _waiter;

    private int _currentTick;
    private WorldSnapshot? _currentSnapshot;
    private WorldSnapshot? _oldestSnapshot;

    // Snapshots extracted from the live chain because they are pinned (e.g. for saving).
    // HashSet gives O(1) add, O(1) remove-per-item, and prevents accidental duplicates.
    private readonly HashSet<WorldSnapshot> _pinnedQueue = new();

    public SimulationLoop(MemorySystem memory, SharedState shared, SimulationCoordinator simulationCoordinator, TClock clock, TWaiter waiter)
    {
        _memory = memory;
        _shared = shared;
        _simulationCoordinator = simulationCoordinator;
        _clock = clock;
        _waiter = waiter;
    }

    public void Run(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        this.Bootstrap();

        var lastTime = _clock.NowNanoseconds;
        long accumulator = 0;

        while (!cancellationToken.IsCancellationRequested)
            this.RunOneIteration(cancellationToken, ref lastTime, ref accumulator);
    }

    private void RunOneIteration(
        CancellationToken cancellationToken,
        ref long lastTime,
        ref long accumulator)
    {
        var now = _clock.NowNanoseconds;
        var delta = Math.Max(0, now - lastTime);
        lastTime = now;
        accumulator += delta;

        this.ProcessAvailableTicks(cancellationToken, ref accumulator);

        _waiter.Wait(new TimeSpan(SimulationConstants.IdleYieldNanoseconds / 100), cancellationToken);
    }

    private void ProcessAvailableTicks(CancellationToken cancellationToken, ref long accumulator)
    {
        while (accumulator >= SimulationConstants.TickDurationNanoseconds)
        {
            this.ApplyPressureDelay(cancellationToken);
            this.Tick(cancellationToken);
            this.CleanupStaleSnapshots();
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
        var image = _memory.RentImage();
        var snapshot = _memory.RentSnapshot();

        snapshot.Initialize(image, tickNumber: 0);

        _currentSnapshot = snapshot;
        _oldestSnapshot = snapshot;
        _currentTick = 0;

        snapshot.MarkPublished(_clock.NowNanoseconds);
        _shared.LatestSnapshot = snapshot;
    }

    // ── Tick ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Advances the world by one tick.
    ///
    /// Rents a fresh image and snapshot (both always succeed — the pool grows
    /// on demand), writes the new world state, links the snapshot onto the chain
    /// via SetNext, and volatile-writes it to LatestSnapshot (release fence —
    /// makes all image writes visible to the consumption thread's subsequent
    /// acquire-read).
    /// </summary>
    private void Tick(CancellationToken cancellationToken)
    {
        var image = _memory.RentImage();
        var snapshot = _memory.RentSnapshot();

        _currentTick++;
        _simulationCoordinator.AdvanceTick(_currentSnapshot!.Image, image, cancellationToken);

        snapshot.Initialize(image, _currentTick);
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
        var consumptionEpoch = _shared.ConsumptionEpoch; // volatile read

        // Walk the chain, freeing every snapshot the consumption side has moved past.
        // Pinned snapshots are severed from the chain and parked in _pinnedQueue so
        // the snapshots after them can still be freed — avoiding the old behaviour of
        // keeping the entire tail alive whenever a single snapshot is pinned.
        while (_oldestSnapshot is not null
               && _oldestSnapshot != _currentSnapshot
               && _oldestSnapshot.TickNumber < consumptionEpoch)
        {
            var toProcess = _oldestSnapshot;
            _oldestSnapshot = toProcess.NextInChain;

            if (_memory.PinnedVersions.IsPinned(toProcess.TickNumber))
            {
                toProcess.ClearNext();
                if (!_pinnedQueue.Add(toProcess))
                    throw new InvalidOperationException("Pinned snapshot was added to the cleanup queue more than once.");
            }
            else
            {
                this.FreeSnapshot(toProcess);
            }
        }

        // Drain any pinned snapshots whose pins have since been released.
        _pinnedQueue.RemoveWhere(snapshot =>
        {
            if (_memory.PinnedVersions.IsPinned(snapshot.TickNumber))
                return false;
            this.FreeSnapshot(snapshot);
            return true;
        });
    }

    private void FreeSnapshot(WorldSnapshot snapshot)
    {
        var image = snapshot.Image; // capture before Release() nulls it
        snapshot.Release();
        Debug.Assert(snapshot.IsUnreferenced, "Snapshot still referenced after cleanup — refcount mismatch.");
        _memory.ReturnSnapshot(snapshot);
        _memory.ReturnImage(image);
    }

    // ── Backpressure ─────────────────────────────────────────────────────────

    /// <summary>
    /// Two-level backpressure gate called before each tick.
    ///
    /// Hard ceiling: if the gap (in nanoseconds) is at or above the hard
    /// ceiling, the simulation blocks in a sleep loop until consumption
    /// catches up enough to drop below the ceiling.
    ///
    /// Soft pressure: once below the ceiling, an exponentially increasing
    /// delay is inserted if the gap still exceeds the low water mark.
    /// See <see cref="SimulationPressure.ComputeDelay"/> for the bucket schedule.
    /// </summary>
    private void ApplyPressureDelay(CancellationToken cancellationToken)
    {
        var gapNanoseconds = (long)(_currentTick - _shared.ConsumptionEpoch)
                            * SimulationConstants.TickDurationNanoseconds;

        while (gapNanoseconds >= SimulationConstants.PressureHardCeilingNanoseconds)
        {
            _waiter.Wait(
                new TimeSpan(SimulationConstants.PressureMaxDelayNanoseconds / 100),
                cancellationToken);
            gapNanoseconds = (long)(_currentTick - _shared.ConsumptionEpoch)
                           * SimulationConstants.TickDurationNanoseconds;
        }

        var delay = SimulationPressure.ComputeDelay(
            gapNanoseconds,
            SimulationConstants.PressureLowWaterMarkNanoseconds,
            SimulationConstants.TickDurationNanoseconds,
            SimulationConstants.PressureBucketCount,
            SimulationConstants.PressureBaseDelayNanoseconds,
            SimulationConstants.PressureMaxDelayNanoseconds);

        if (delay > 0)
            _waiter.Wait(new TimeSpan(delay / 100), cancellationToken);
    }

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor
    {
        private readonly SimulationLoop<TClock, TWaiter> _loop;

        public TestAccessor(SimulationLoop<TClock, TWaiter> loop) => _loop = loop;

        public void Bootstrap() => _loop.Bootstrap();

        public void Tick() => _loop.Tick(CancellationToken.None);

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
