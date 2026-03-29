using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Performs the actual save operation for a snapshot image.
/// Separated from save dispatch so tests can control success and failure
/// without depending on thread-pool behavior.
/// </summary>
internal interface ISaver
{
    void Save(WorldImage image, int tick);
}
