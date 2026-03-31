using Simulation.Memory;
using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Adapts the simulation-specific tick logic to the generic
/// <see cref="IProducer{TPayload}"/> interface.
///
/// Owns the <see cref="ObjectPool{WorldImage}"/> (image allocation is a
/// producer concern) and delegates parallel tick work to the
/// <see cref="SimulationCoordinator"/>.
/// </summary>
internal struct SimulationProducer : IProducer<WorldImage>
{
    private readonly ObjectPool<WorldImage> _imagePool;
    private readonly SimulationCoordinator _coordinator;

    public SimulationProducer(ObjectPool<WorldImage> imagePool, SimulationCoordinator coordinator)
    {
        _imagePool = imagePool;
        _coordinator = coordinator;
    }

    public WorldImage Bootstrap(CancellationToken cancellationToken) =>
        _imagePool.Rent();

    public WorldImage Produce(WorldImage current, CancellationToken cancellationToken)
    {
        var image = _imagePool.Rent();
        _coordinator.AdvanceTick(current, image, cancellationToken);
        return image;
    }

    public void ReleaseResources(WorldImage payload)
    {
        payload.ResetForPool();
        _imagePool.Return(payload);
    }
}
