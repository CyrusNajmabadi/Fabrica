using Engine.Pipeline;
using Engine.World;

namespace Engine.Rendering;

/// <summary>
/// Adapts the simulation-specific rendering/display logic to the generic <see cref="IConsumer{TPayload}"/> interface.
///
/// Owns the <see cref="RenderCoordinator"/> that dispatches parallel render work. Builds a <see cref="RenderFrame"/> from the
/// generic previous/latest pair and calls the domain renderer.
/// </summary>
internal struct RenderConsumer<TRenderer>(int workerCount, TRenderer renderer) : IConsumer<WorldImage>
    where TRenderer : struct, IRenderer
{
    private readonly RenderCoordinator _renderCoordinator = new(workerCount);
#pragma warning disable IDE0044 // Mutable struct — readonly would cause defensive copies
    private TRenderer _renderer = renderer;
#pragma warning restore IDE0044

    public void Consume(
        BaseProductionLoop<WorldImage>.ChainNode previous,
        BaseProductionLoop<WorldImage>.ChainNode latest,
        long frameStartNanoseconds,
        CancellationToken cancellationToken)
    {
        var frame = new RenderFrame
        {
            Previous = previous,
            Latest = latest,
            Interpolation = new InterpolationClock
            {
                ElapsedNanoseconds = frameStartNanoseconds - latest.PublishTimeNanoseconds,
                TickDurationNanoseconds = SimulationConstants.TickDurationNanoseconds,
            },
            EngineStatus = default,
        };

        _renderCoordinator.DispatchFrame(in frame, cancellationToken);
        _renderer.Render(in frame);
    }
}
