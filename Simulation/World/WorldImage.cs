namespace Simulation.World;

/// <summary>
/// The raw world state for one simulation tick.
///
/// OWNERSHIP AND THREAD VISIBILITY
///   WorldImage instances are owned by the simulation thread via the image pool.
///   The simulation writes all fields, then volatile-writes the containing
///   WorldSnapshot to SharedState.LatestSnapshot (a release fence).
///   The consumption thread volatile-reads LatestSnapshot (an acquire fence) and
///   then reads image fields — the release/acquire pair guarantees all simulation
///   writes are visible without any further synchronisation.
///
///   Once published, the image is treated as immutable until the simulation
///   reclaims it.  The save task may read it concurrently while pinned, but
///   never writes to it, so no additional protection is needed.
///
/// Future: will hold belt state, machine state, etc. backed by a persistent
/// tree structure so that consecutive ticks share unchanged subtrees, keeping
/// memory use proportional to the number of changes per tick rather than
/// total world size.
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
