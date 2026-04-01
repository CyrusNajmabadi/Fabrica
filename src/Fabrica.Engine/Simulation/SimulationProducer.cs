using Fabrica.Engine.World;
using Fabrica.Pipeline;
using Fabrica.Pipeline.Memory;

namespace Fabrica.Engine.Simulation;

/// <summary>
/// Adapts the simulation-specific tick logic to the generic <see cref="IProducer{TPayload}"/> interface.
///
/// Owns the image pool (allocation is a producer concern) and the <see cref="SimulationCoordinator"/> that dispatches parallel
/// tick work. The pool's allocator handles <see cref="WorldImage.ResetForPool"/> on return, so <see cref="ReleaseResources"/>
/// just returns to the pool.
/// </summary>
internal readonly struct SimulationProducer(ObjectPool<WorldImage, WorldImage.Allocator> imagePool, int workerCount) : IProducer<WorldImage>
{
    private readonly ObjectPool<WorldImage, WorldImage.Allocator> _imagePool = imagePool;
    private readonly SimulationCoordinator _coordinator = new(workerCount);

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
