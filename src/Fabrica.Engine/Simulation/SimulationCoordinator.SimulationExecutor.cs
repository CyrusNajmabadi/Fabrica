using Fabrica.Pipeline.Threading;

namespace Fabrica.Engine.Simulation;

internal sealed partial class SimulationCoordinator
{
    /// <summary>
    /// Domain-specific executor for simulation tick work. Each simulation worker thread owns one instance, which holds the
    /// worker's private <see cref="WorkerResources"/>.
    ///
    /// <see cref="Prepare"/> clears per-tick accumulation state before each dispatch. <see cref="Execute"/> performs the worker's
    /// portion of the tick computation.
    ///
    /// DETERMINISM CONTRACT
    ///   All workers run in parallel on the same tick and then join before the coordinator proceeds. For the simulation to be
    ///   deterministic, one of the following must hold:
    ///
    ///   1. DETERMINISTIC SPLIT / MERGE — each worker operates on an overlapping
    ///      or shared region, and the coordinator merges results in a fixed order (worker 0 before worker 1, etc.) regardless of
    ///      completion order.
    ///
    ///   2. INDEPENDENT PARTITIONS — the world is partitioned so that each
    ///      worker's writes are disjoint from every other worker's reads and writes. In this case merge order is irrelevant
    ///      because the partitions cannot influence each other.
    ///
    ///   The current design targets approach (2): each worker writes into a disjoint region of <c>state.NextImage</c> and reads
    ///   only from the immutable <c>state.PreviousImage</c>. Cross-partition effects (e.g. items crossing a partition boundary)
    ///   should be handled in a single- threaded fixup pass after the join, or deferred to the next tick.
    /// </summary>
    public readonly struct SimulationExecutor(WorkerResources resources) : IThreadExecutor<SimulationTickState>
    {
        public readonly WorkerResources Resources = resources;

        public void Prepare() =>
            Resources.PrepareForTick();

        public void Execute(in SimulationTickState state, CancellationToken cancellationToken)
        {
            // TODO: actual per-worker tick computation. Read state.PreviousImage (immutable), write into state.NextImage
            // partition, log new shared nodes into Resources.CreatedNodes. Check cancellationToken for early exit during long
            // work.
        }
    }
}
