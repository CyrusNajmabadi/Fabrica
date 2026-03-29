using Simulation.World;

namespace Simulation.Memory;

/// <summary>
/// Owns all object pools and the cross-thread pinned-versions set.
/// Pool access (Rent/Return) is single-threaded (simulation thread only).
/// PinnedVersions is thread-safe and may be accessed from any thread.
/// </summary>
internal sealed class MemorySystem
{
    private readonly ObjectPool<WorldSnapshot> _snapshots;
    private readonly ObjectPool<WorldImage>    _images;

    public PinnedVersions PinnedVersions { get; } = new();

    public MemorySystem(int poolSize)
    {
        _snapshots = new ObjectPool<WorldSnapshot>(poolSize);
        _images    = new ObjectPool<WorldImage>(poolSize);
    }

    // ── Snapshot pool (simulation thread only) ───────────────────────────────

    public WorldSnapshot? RentSnapshot() => _snapshots.Rent();

    public void ReturnSnapshot(WorldSnapshot s)
    {
        // s.Release() must have already been called; Image/Next are null at this point.
        _snapshots.Return(s);
    }

    // ── Image pool (simulation thread only) ──────────────────────────────────

    public WorldImage? RentImage() => _images.Rent();

    public void ReturnImage(WorldImage img)
    {
        img.Reset();
        _images.Return(img);
    }

    // ── Pressure (simulation thread reads, informational) ────────────────────

    /// <summary>
    /// Overall pool pressure: fraction of snapshot slots currently rented.
    /// 0.0 = all free, 1.0 = fully exhausted.
    /// </summary>
    public double Pressure => _snapshots.Pressure;

    public int SnapshotsAvailable => _snapshots.Available;
}
