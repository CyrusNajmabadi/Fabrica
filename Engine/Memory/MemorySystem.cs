using Engine.Pipeline;

namespace Engine.Memory;

/// <summary>
/// Owns all object pools and the cross-thread pinned-versions set.
///
/// SINGLE-THREAD POOL OWNERSHIP
///   Both ObjectPool instances (nodes and payloads) are accessed exclusively
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
///
/// ALLOCATOR STRATEGY
///   Both pools use struct-generic allocators (<typeparamref name="TPayloadAllocator"/>
///   and <see cref="BaseProductionLoop{TPayload}.NodeAllocator"/>) so the JIT
///   specialises all allocation and reset paths, eliminating interface dispatch entirely.
/// </summary>
internal sealed class MemorySystem<TPayload, TPayloadAllocator>
    where TPayload : class
    where TPayloadAllocator : struct, IAllocator<TPayload>
{
    private readonly ObjectPool<BaseProductionLoop<TPayload>.ChainNode, BaseProductionLoop<TPayload>.NodeAllocator> _nodePool;
    private readonly ObjectPool<TPayload, TPayloadAllocator> _payloadPool;

    public PinnedVersions PinnedVersions { get; } = new();

    public MemorySystem(int initialPoolSize, TPayloadAllocator payloadAllocator = default)
    {
        if (initialPoolSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialPoolSize));

        _nodePool = new ObjectPool<BaseProductionLoop<TPayload>.ChainNode, BaseProductionLoop<TPayload>.NodeAllocator>(initialPoolSize);
        _payloadPool = new ObjectPool<TPayload, TPayloadAllocator>(initialPoolSize, payloadAllocator);
    }

    // ── Node pool (simulation thread only) ───────────────────────────────────

    public BaseProductionLoop<TPayload>.ChainNode RentNode() => _nodePool.Rent();
    public void ReturnNode(BaseProductionLoop<TPayload>.ChainNode node) => _nodePool.Return(node);

    // ── Payload pool (simulation thread only) ────────────────────────────────

    public TPayload RentPayload() => _payloadPool.Rent();
    public void ReturnPayload(TPayload payload) => _payloadPool.Return(payload);
}
