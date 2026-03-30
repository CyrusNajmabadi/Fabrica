using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Performs the actual (blocking) save operation for a snapshot image.
/// Implementations are constrained to struct for zero interface-dispatch overhead.
///
/// Called from the save task thread (threadpool), not the consumption thread.
/// The image is guaranteed pinned for the duration of this call — the simulation
/// will not reclaim it until ISaveRunner's callback returns.
///
/// Separated from save dispatch (ISaveRunner) so tests can drive success and
/// failure independently, without depending on thread-pool behaviour.
/// </summary>
internal interface ISaver
{
    void Save(WorldImage image, int tick);
}
