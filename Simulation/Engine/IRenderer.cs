using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Performs the render operation for the latest published snapshot.
/// Separated from the consumption loop so tests can observe render sequencing
/// without depending on console output.
/// </summary>
internal interface IRenderer
{
    void Render(WorldSnapshot snapshot);
}
