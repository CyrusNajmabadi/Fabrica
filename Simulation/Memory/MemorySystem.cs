using Simulation.World;

namespace Simulation.Memory;

/// <summary>
/// Owns all object pools and the cross-thread pinned-versions set.
/// Pool access (Rent/Return) is single-threaded (simulation thread only).
/// <see cref="PinnedVersions"/> is thread-safe and may be accessed from any thread.
/// </summary>
internal sealed class MemorySystem
{
    private readonly ObjectPool<WorldSnapshot> _snapshotPool;
    private readonly ObjectPool<WorldImage>    _imagePool;

    public PinnedVersions PinnedVersions { get; } = new();

    public MemorySystem(int poolSize)
    {
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
