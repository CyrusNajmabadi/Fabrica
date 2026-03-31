using Engine.Memory;

namespace Engine.Pipeline;

/// <summary>
/// Allocator for <see cref="ChainNode{TPayload}"/> instances managed by an
/// <see cref="ObjectPool{T, TAllocator}"/>.  Reset is a no-op because the
/// production loop handles all node lifecycle cleanup (ClearPayload, Release)
/// before returning nodes to the pool.
/// </summary>
internal struct ChainNodeAllocator<TPayload> : IAllocator<ChainNode<TPayload>>
{
    public ChainNode<TPayload> Allocate() => new();

    public void Reset(ChainNode<TPayload> item) { }
}
