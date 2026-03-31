using Simulation.Memory;
using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Adapts the simulation-specific tick logic to the generic
/// <see cref="IProducer{TNode}"/> interface.
///
/// Owns the <see cref="ObjectPool{WorldImage}"/> (image allocation is a
/// producer concern) and delegates parallel tick work to the
/// <see cref="SimulationCoordinator"/>.
/// </summary>
internal struct SimulationProducer : IProducer<WorldSnapshot>
{
    private readonly ObjectPool<WorldImage> _imagePool;
    private readonly SimulationCoordinator _coordinator;

    public SimulationProducer(ObjectPool<WorldImage> imagePool, SimulationCoordinator coordinator)
    {
        _imagePool = imagePool;
        _coordinator = coordinator;
    }

    public void Bootstrap(WorldSnapshot node, CancellationToken cancellationToken) =>
        node.Image = _imagePool.Rent();

    public void Produce(WorldSnapshot current, WorldSnapshot next, CancellationToken cancellationToken)
    {
        var image = _imagePool.Rent();
        _coordinator.AdvanceTick(current.Image, image, cancellationToken);
        next.Image = image;
    }

    public void ReleaseResources(WorldSnapshot node)
    {
        var image = node.Image;
        image.ResetForPool();
        _imagePool.Return(image);
    }
}
