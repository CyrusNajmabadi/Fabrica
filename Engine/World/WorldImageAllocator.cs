using Engine.Memory;

namespace Engine.World;

/// <summary>
/// Allocator for <see cref="WorldImage"/> instances managed by an
/// <see cref="ObjectPool{T, TAllocator}"/>.  Calls
/// <see cref="WorldImage.ResetForPool"/> on return so pooled instances
/// always start with a clean slate.
/// </summary>
internal struct WorldImageAllocator : IAllocator<WorldImage>
{
    public WorldImage Allocate() => new();

    public void Reset(WorldImage item) => item.ResetForPool();
}
