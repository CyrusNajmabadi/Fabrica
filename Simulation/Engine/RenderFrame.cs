using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Everything the renderer needs for one frame: the simulation snapshots
/// (previous and current) plus non-simulation engine state (save status,
/// diagnostics, etc.).
///
/// Bundling these into a single struct keeps the <see cref="IRenderer.Render"/>
/// signature stable as new data is added — callers never need a parade of
/// additional parameters.
/// </summary>
internal readonly struct RenderFrame
{
    public required WorldSnapshot? Previous { get; init; }
    public required WorldSnapshot Current { get; init; }
    public required EngineStatus EngineStatus { get; init; }
}
