using Engine.Memory;
using Engine.Pipeline;
using Engine.World;

namespace Engine.Simulation;

/// <summary>
/// Adapts the simulation-specific tick logic to the generic
/// <see cref="IProducer{TPayload}"/> interface.
///
/// Owns the image pool (allocation is a producer concern) and delegates
/// parallel tick work to the <see cref="SimulationCoordinator"/>.
/// The pool's allocator handles <see cref="WorldImage.ResetForPool"/> on
/// return, so <see cref="ReleaseResources"/> just returns to the pool.
/// </summary>
internal struct SimulationProducer : IProducer<WorldImage>
{
    private readonly ObjectPool<WorldImage, WorldImage.Allocator> _imagePool;
    private readonly SimulationCoordinator _coordinator;

    public SimulationProducer(ObjectPool<WorldImage, WorldImage.Allocator> imagePool, SimulationCoordinator coordinator)
    {
        _imagePool = imagePool;
        _coordinator = coordinator;
    }

    public WorldImage CreateInitialPayload(CancellationToken cancellationToken) =>
        _imagePool.Rent();

    public WorldImage Produce(WorldImage current, CancellationToken cancellationToken)
    {
        var image = _imagePool.Rent();
        _coordinator.AdvanceTick(current, image, cancellationToken);
        return image;
    }

    public void ReleaseResources(WorldImage payload) =>
        _imagePool.Return(payload);
}
