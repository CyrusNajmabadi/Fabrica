using Engine.Threading;

namespace Engine.Simulation;

internal sealed partial class SimulationCoordinator
{
    /// <summary>
    /// Domain-specific executor for simulation tick work.  Each simulation worker
    /// thread owns one instance, which holds the worker's private
    /// <see cref="WorkerResources"/>.
    ///
    /// <see cref="Prepare"/> clears per-tick accumulation state before each dispatch.
    /// <see cref="Execute"/> performs the worker's portion of the tick computation.
    /// </summary>
    public readonly struct SimulationExecutor(WorkerResources resources) : IThreadExecutor<SimulationTickState>
    {
        public readonly WorkerResources Resources = resources;

        public void Prepare() =>
            Resources.PrepareForTick();

        public void Execute(in SimulationTickState state, CancellationToken cancellationToken)
        {
            // TODO: actual per-worker tick computation.
            // Read state.PreviousImage (immutable), write into state.NextImage partition,
            // log new shared nodes into Resources.CreatedNodes.
            // Check cancellationToken for early exit during long work.
        }
    }
}
