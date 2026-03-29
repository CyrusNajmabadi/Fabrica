using Simulation.World;

namespace Simulation.Memory;

/// <summary>
/// Owns all object pools and the cross-thread pinned-versions set.
///
/// SINGLE-THREAD POOL OWNERSHIP
///   Both ObjectPool instances (snapshots and images) are accessed exclusively
///   from the simulation thread.  This is intentional: the simulation is the sole
///   memory manager, which eliminates all locking, atomic operations, and ABA
///   hazards from the allocation fast path.
///
///   Other threads (consumption, save task) interact with memory only indirectly:
///   the consumption thread reads the WorldSnapshot reference published by the
///   simulation; the save task reads the WorldImage inside that snapshot.  Neither
///   ever calls Rent or Return — the objects are always reclaimed by the simulation
///   after both threads have finished with them.
///
/// EXCEPTION — PinnedVersions
///   PinnedVersions is thread-safe and may be called from any thread.  It is the
///   only part of the memory system that crosses thread boundaries with mutable
///   state.  See PinnedVersions for the full explanation of why concurrent access
///   is required there and why the overhead is still negligible.
/// </summary>
internal sealed class MemorySystem
{
    private readonly ObjectPool<WorldSnapshot> _snapshotPool;
    private readonly ObjectPool<WorldImage>    _imagePool;

    public PinnedVersions PinnedVersions { get; } = new();

    public MemorySystem(int poolSize)
    {
        if (poolSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(poolSize));

        if (poolSize % SimulationConstants.PressureBucketCount != 0)
            throw new ArgumentException(
                $"Pool size must be a multiple of {SimulationConstants.PressureBucketCount}.",
                nameof(poolSize));

        _snapshotPool = new ObjectPool<WorldSnapshot>(poolSize);
        _imagePool    = new ObjectPool<WorldImage>(poolSize);
    }

    // ── Snapshot pool (simulation thread only) ───────────────────────────────

    public WorldSnapshot? RentSnapshot()                  => _snapshotPool.Rent();
    public void           ReturnSnapshot(WorldSnapshot snapshot) => _snapshotPool.Return(snapshot);

    // ── Image pool (simulation thread only) ──────────────────────────────────

    public WorldImage? RentImage()              => _imagePool.Rent();
    public void        ReturnImage(WorldImage image) { image.Reset(); _imagePool.Return(image); }

    // ── Pool capacity (simulation thread reads) ───────────────────────────────

    public int SnapshotsAvailable    => _snapshotPool.Available;
    public int SnapshotPoolCapacity  => _snapshotPool.Capacity;
}
