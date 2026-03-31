using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Adapts the simulation-specific rendering/display logic to the generic
/// <see cref="IConsumer{TNode}"/> interface.
///
/// Builds a <see cref="RenderFrame"/> from the generic previous/latest pair,
/// dispatches to parallel render workers via <see cref="RenderCoordinator"/>,
/// and calls the domain renderer.
/// </summary>
internal struct SimulationConsumer<TRenderer> : IConsumer<WorldSnapshot>
    where TRenderer : struct, IRenderer
{
    private readonly RenderCoordinator _renderCoordinator;
    private TRenderer _renderer;

    public SimulationConsumer(RenderCoordinator renderCoordinator, TRenderer renderer)
    {
        _renderCoordinator = renderCoordinator;
        _renderer = renderer;
    }

    public void Consume(
        WorldSnapshot? previous,
        WorldSnapshot latest,
        long frameStartNanoseconds,
        SaveStatus saveStatus,
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
            EngineStatus = new EngineStatus
            {
                Save = saveStatus,
            },
        };

        _renderCoordinator.DispatchFrame(in frame, cancellationToken);
        _renderer.Render(in frame);
    }
}
