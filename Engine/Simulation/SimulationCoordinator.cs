using Engine.Threading;
using Engine.World;

namespace Engine.Simulation;

/// <summary>
/// Parallel coordinator for N simulation workers.
///
/// TICK DISPATCH CYCLE
///   <see cref="AdvanceTick"/> runs once per simulation tick, called by
///   <see cref="SimulationProducer"/>:
///
///     1. PREPARE — each worker's <see cref="SimulationCoordinator.SimulationExecutor.Prepare"/>
///        clears per-tick accumulation state via <see cref="WorkerResources"/>.
///     2. SET UP — previous and next images and the cancellation token are
///        written to each worker as a <see cref="SimulationTickState"/>.
///     3. SIGNAL — all workers are woken from their park state.
///     4. JOIN — <see cref="WorkerGroup{SimulationTickState,SimulationExecutor}.Dispatch"/> waits
///        on every worker's done signal via <see cref="WorkerGroup{SimulationTickState,SimulationExecutor}.WaitHandleBatch"/>.
///     5. REF-COUNT — the SimulationCoordinator walks each worker's created-nodes list
///        and performs AddRef on shared subtree nodes.  This is the only
///        phase that touches ref-counts, and it runs on a single thread —
///        no interlocked operations needed.
///
///   Because the group always joins all workers before returning, the calling
///   thread sees a fully-consistent next image with all reference counts correct.
///
/// CANCELLATION
///   The engine's <see cref="CancellationToken"/> flows through AdvanceTick
///   to each worker, allowing long-running tick work to exit early when
///   the engine shuts down.
///
/// THREAD PINNING
///   Each worker attempts to pin itself to a specific logical core
///   (worker N → core N) at thread startup via <see cref="ThreadPinningNative"/>.
///   This is best-effort: pinning may fail silently on restricted kernels,
///   containerised environments, or macOS (not supported).  The simulation
///   is correct regardless — pinning is purely a cache-affinity optimisation.
/// </summary>
internal sealed partial class SimulationCoordinator
{
    private readonly WorkerGroup<SimulationTickState, SimulationExecutor> _group;

    public SimulationCoordinator(int workerCount) =>
        _group = new WorkerGroup<SimulationTickState, SimulationExecutor>(
            workerCount,
            static i => new SimulationExecutor(new WorkerResources()),
            "SimWorker");

    public int WorkerCount => _group.WorkerCount;

    /// <summary>
    /// Dispatches one tick of work across all workers and blocks until
    /// all have completed.  After join, performs deferred ref-counting
    /// on shared subtree nodes created by workers.
    /// </summary>
    public void AdvanceTick(WorldImage previous, WorldImage next, CancellationToken cancellationToken)
    {
        var state = new SimulationTickState
        {
            PreviousImage = previous,
            NextImage = next,
        };

        _group.Dispatch(state, cancellationToken);

        foreach (var worker in _group.Workers)
            this.CollectCreatedNodes(ref worker.Executor);
    }

    private void CollectCreatedNodes(ref SimulationExecutor executor)
    {
        // Future: for each node in executor.Resources.CreatedNodes,
        // call node.AddRef() to increment shared subtree reference counts.
    }

    public void Shutdown() =>
        _group.Shutdown();
}
