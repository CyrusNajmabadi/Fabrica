using Fabrica.Core.Memory;

namespace Fabrica.Engine.World;

/// <summary>
/// The raw world state for one simulation tick.
///
/// OWNERSHIP AND THREAD VISIBILITY
///   WorldImage instances are owned by the simulation thread via the image pool. The simulation writes all fields, then appends a
///   PipelineEntry containing the image to the ProducerConsumerQueue (volatile write of producer position — a release fence). The
///   consumption thread acquires entries (volatile read of producer position — an acquire fence) and then reads image fields — the
///   release/acquire pair guarantees all simulation writes are visible without any further synchronisation.
///
///   Once published, the image is treated as immutable until the simulation reclaims it. The save task may read it concurrently
///   while pinned, but never writes to it, so no additional protection is needed.
///
/// Future: will hold belt state, machine state, etc. backed by a persistent tree structure so that consecutive ticks share
/// unchanged subtrees, keeping memory use proportional to the number of changes per tick rather than total world size.
/// </summary>
public sealed class WorldImage
{
    // TODO: belt state, machine state, etc.

    /// <summary>
    /// Clears all simulation state so a pooled instance starts with a clean slate. Called by <see cref="Allocator.Reset"/> when
    /// the pool reclaims the image.
    /// </summary>
    public void ResetForPool()
    {
    }

    /// <summary>
    /// Allocator for <see cref="WorldImage"/> instances managed by an <see cref="ObjectPool{T, TAllocator}"/>. Calls
    /// <see cref="ResetForPool"/> on return so pooled instances always start with a clean slate.
    /// </summary>
    public readonly struct Allocator : IAllocator<WorldImage>
    {
        public readonly WorldImage Allocate()
            => new();

        public readonly void Reset(WorldImage item)
            => item.ResetForPool();
    }
}
