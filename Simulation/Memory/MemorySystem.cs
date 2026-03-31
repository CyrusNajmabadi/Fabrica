using Simulation.World;

namespace Simulation.Memory;

/// <summary>
/// Owns all object pools and the cross-thread pinned-versions set.
///
/// SINGLE-THREAD POOL OWNERSHIP
///   Both ObjectPool instances (nodes and images) are accessed exclusively
///   from the simulation thread.  This is intentional: the simulation is the sole
///   memory manager, which eliminates all locking, atomic operations, and ABA
///   hazards from the allocation fast path.
///
///   Other threads (consumption, deferred consumers) interact with memory only
///   indirectly: the consumption thread reads the ChainNode reference published
///   by the simulation; deferred consumer tasks read the payload inside the node.
///   Neither ever calls Rent or Return — the objects are always reclaimed by the
///   simulation after both threads have finished with them.
///
/// EXCEPTION — PinnedVersions
///   PinnedVersions is thread-safe and may be called from any thread.  It is the
///   only part of the memory system that crosses thread boundaries with mutable
///   state.  See PinnedVersions for the full explanation of why concurrent access
///   is required there and why the overhead is still negligible.
/// </summary>
internal sealed class MemorySystem
{
    private readonly ObjectPool<ChainNode<WorldImage>> _nodePool;
    private readonly ObjectPool<WorldImage> _imagePool;

    public PinnedVersions PinnedVersions { get; } = new();

    public MemorySystem(int initialPoolSize = SimulationConstants.SnapshotPoolSize)
    {
        if (initialPoolSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialPoolSize));

        _nodePool = new ObjectPool<ChainNode<WorldImage>>(initialPoolSize);
        _imagePool = new ObjectPool<WorldImage>(initialPoolSize);
    }

    // ── Node pool (simulation thread only) ───────────────────────────────────

    public ChainNode<WorldImage> RentNode() => _nodePool.Rent();
    public void ReturnNode(ChainNode<WorldImage> node) => _nodePool.Return(node);

    // ── Image pool (simulation thread only) ──────────────────────────────────

    public WorldImage RentImage() => _imagePool.Rent();
    public void ReturnImage(WorldImage image) { image.ResetForPool(); _imagePool.Return(image); }
}
