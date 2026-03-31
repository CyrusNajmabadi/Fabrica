namespace Engine.Memory;

/// <summary>
/// Owns the payload object pool.
///
/// SINGLE-THREAD POOL OWNERSHIP
///   The ObjectPool instance is accessed exclusively from the simulation thread.
///   This is intentional: the simulation is the sole memory manager, which
///   eliminates all locking, atomic operations, and ABA hazards from the
///   allocation fast path.
///
///   Other threads (consumption, deferred consumers) interact with memory only
///   indirectly: the consumption thread reads the ChainNode reference published
///   by the simulation; deferred consumer tasks read the payload inside the node.
///   Neither ever calls Rent or Return — the objects are always reclaimed by the
///   simulation after both threads have finished with them.
///
///   Chain node pooling is owned by <see cref="Pipeline.BaseProductionLoop{TPayload}"/>
///   directly, since only the production loop allocates and frees nodes.
///
/// ALLOCATOR STRATEGY
///   The pool uses a struct-generic allocator (<typeparamref name="TPayloadAllocator"/>)
///   so the JIT specialises all allocation and reset paths, eliminating interface
///   dispatch entirely.
/// </summary>
internal sealed class MemorySystem<TPayload, TPayloadAllocator>
    where TPayload : class
    where TPayloadAllocator : struct, IAllocator<TPayload>
{
    private readonly ObjectPool<TPayload, TPayloadAllocator> _payloadPool;

    public MemorySystem(int initialPoolSize, TPayloadAllocator payloadAllocator = default)
    {
        if (initialPoolSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialPoolSize));

        _payloadPool = new ObjectPool<TPayload, TPayloadAllocator>(initialPoolSize, payloadAllocator);
    }

    // ── Payload pool (simulation thread only) ────────────────────────────────

    public TPayload RentPayload() => _payloadPool.Rent();
    public void ReturnPayload(TPayload payload) => _payloadPool.Return(payload);
}
