namespace Simulation.World;

/// <summary>
/// Immutable snapshot of all world state at a given tick.
/// Owned exclusively by the simulation thread via the snapshot pool.
/// Future: will hold belt state, machine state, etc. backed by a persistent tree.
/// </summary>
internal sealed class WorldImage
{
    public int TickNumber;

    // TODO: belt state, machine state, etc.

    internal void Reset()
    {
        TickNumber = 0;
    }
}
