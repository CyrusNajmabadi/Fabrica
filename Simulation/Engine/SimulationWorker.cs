using Simulation.Memory;

namespace Simulation.Engine;

/// <summary>
/// Design stub for a future per-worker simulation context.
///
/// MULTI-THREADED SIMULATION MODEL
///   The simulation manager partitions tick computation across N workers, each
///   running on its own thread.  Every worker owns:
///
///     • A dedicated <see cref="ObjectPool{T}"/> for allocating tree nodes (and
///       any other per-tick objects) without contention.  Because pools are
///       single-threaded, no locking or interlocked operations are needed on
///       the allocation fast path.
///
///     • A "created nodes" list that records every shared object the worker
///       allocated during the current tick.  After all workers complete, the
///       main simulation thread walks each worker's list and performs AddRef
///       calls on behalf of the worker.  This deferred ref-counting keeps
///       reference-count mutations on a single thread, avoiding interlocked
///       operations entirely.
///
/// THREADING CONTRACT
///   • During tick computation, a worker may only touch its own pool and its
///     own created-nodes list.  No shared mutable state is accessed.
///   • Between ticks (the "join" phase), only the main simulation thread
///     reads the created-nodes lists and adjusts reference counts.
///   • Workers must not carry references to objects from a previous tick's
///     pool — each tick starts with a clean created-nodes list.
///
/// BACKPRESSURE
///   Backpressure is driven by the tick-epoch gap measured on the main
///   simulation thread (see <see cref="SimulationPressure"/>).  Workers
///   themselves do not throttle; the main thread simply delays before
///   dispatching the next tick to all workers.
///
/// This class is a placeholder — it contains no thread management yet.
/// Its purpose is to document the intended ownership model and provide a
/// home for per-worker state as the multi-threaded simulation is built out.
/// </summary>
internal sealed class SimulationWorker
{
    // Future: ObjectPool<TreeNode> _nodePool;
    // Future: List<TreeNode> _createdNodes;
}
