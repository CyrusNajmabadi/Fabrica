using Engine.Pipeline;
using Engine.World;

namespace Engine.Rendering;

/// <summary>
/// Adapts the simulation-specific rendering/display logic to the generic
/// <see cref="IConsumer{TPayload}"/> interface.
///
/// Builds a <see cref="RenderFrame"/> from the generic previous/latest pair,
/// dispatches to parallel render workers via <see cref="RenderCoordinator"/>,
/// and calls the domain renderer.
/// </summary>
internal struct RenderConsumer<TRenderer> : IConsumer<WorldImage>
    where TRenderer : struct, IRenderer
{
    private readonly RenderCoordinator _renderCoordinator;
    private TRenderer _renderer;

    public RenderConsumer(RenderCoordinator renderCoordinator, TRenderer renderer)
    {
        _renderCoordinator = renderCoordinator;
        _renderer = renderer;
    }

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
