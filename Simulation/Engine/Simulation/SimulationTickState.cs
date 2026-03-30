using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Per-tick input data written by the <see cref="SimulationCoordinator"/> before dispatching
/// work to simulation workers.  Both images are set once per tick and read by
/// every worker — <see cref="PreviousImage"/> is immutable (read-only),
/// <see cref="NextImage"/> is the fresh image being populated.
/// </summary>
internal readonly struct SimulationTickState
{
    public required WorldImage PreviousImage { get; init; }
    public required WorldImage NextImage { get; init; }
}
