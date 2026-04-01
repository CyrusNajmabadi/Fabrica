namespace Fabrica.Engine.Simulation;

/// <summary>
/// Per-worker resource bundle for multi-threaded simulation.
///
/// Each <see cref="SimulationWorker"/> owns a dedicated WorkerResources instance, giving it exclusive access to pools and
/// creation-tracking lists during tick computation — no locking required.
///
/// FUTURE CONTENTS
///   As WorldImage gains real state (belts, machines, subtree nodes), this class will hold:
///     • Per-type ObjectPools for local allocation without contention.
///     • A "created nodes" list for deferred ref-counting — the SimulationCoordinator
///       walks each worker's list after all workers complete and performs AddRef calls on shared subtree nodes from a single
///       thread.
///
/// TICK LIFECYCLE
///   Before each tick dispatch, <see cref="PrepareForTick"/> clears any per-tick accumulation state (e.g. the created-nodes list)
///   so each tick begins with a clean slate.
/// </summary>
internal sealed class WorkerResources
{
    // Future: ObjectPool<BeltSegment> BeltPool Future: ObjectPool<TreeNode> NodePool Future: List<SharedNode> CreatedNodes

    /// <summary>
    /// Resets per-tick state in preparation for a new tick dispatch. Called by the <see cref="SimulationCoordinator"/> before
    /// signaling workers.
    /// </summary>
    public void PrepareForTick()
    {
        // Future: CreatedNodes.Clear(), etc.
    }
}
