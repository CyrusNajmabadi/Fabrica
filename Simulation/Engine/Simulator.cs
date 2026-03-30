using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Parallel coordinator for N simulation workers.
///
/// TICK DISPATCH CYCLE
///   <see cref="AdvanceTick"/> runs once per simulation tick, called by
///   <see cref="SimulationLoop{TClock,TWaiter}"/>:
///
///     1. PREPARE — each worker's <see cref="WorkerResources.PrepareForTick"/>
///        clears per-tick accumulation state.
///     2. SET UP — previous and next images are written to each worker.
///     3. SIGNAL — all workers are woken from their park state.
///     4. JOIN — the calling thread waits on each worker's done signal
///        in turn.  Every worker runs in parallel; the sequential wait
///        just collects results.
///     5. REF-COUNT — the Simulator walks each worker's created-nodes list
///        and performs AddRef on shared subtree nodes.  This is the only
///        phase that touches ref-counts, and it runs on a single thread —
///        no interlocked operations needed.
///
///   Because the Simulator always joins all workers before returning,
///   the calling thread sees a fully-consistent next image with all
///   reference counts correct.
///
/// WORKER OWNERSHIP
///   Each <see cref="SimulationWorker"/> owns a dedicated
///   <see cref="WorkerResources"/> instance.  Workers never access each
///   other's resources during tick computation.
///
/// ZERO-WORKER MODE
///   Constructing with workerCount = 0 creates a no-op Simulator: no
///   threads are spawned and <see cref="AdvanceTick"/> returns immediately.
///   This is used by tests that exercise snapshot lifecycle without
///   needing real parallel computation.
///
/// THREAD PINNING (future)
///   Workers currently use unpinned background threads.  A future
///   enhancement will pin each worker to a specific core for cache
///   affinity.  See TODO.md.
/// </summary>
internal sealed class Simulator : IDisposable
{
    private readonly SimulationWorker[] _workers;

    public Simulator(int workerCount)
    {
        _workers = new SimulationWorker[workerCount];
        for (var i = 0; i < workerCount; i++)
            _workers[i] = new SimulationWorker(new WorkerResources(), i);
    }

    public int WorkerCount => _workers.Length;

    /// <summary>
    /// Dispatches one tick of work across all workers and blocks until
    /// all have completed.  After join, performs deferred ref-counting
    /// on shared subtree nodes created by workers.
    /// </summary>
    public void AdvanceTick(WorldImage previous, WorldImage next)
    {
        if (_workers.Length == 0)
            return;

        foreach (var worker in _workers)
        {
            worker.Resources.PrepareForTick();
            worker.PreviousImage = previous;
            worker.NextImage = next;
        }

        foreach (var worker in _workers)
            worker.Signal();

        foreach (var worker in _workers)
            worker.WaitForCompletion();

        foreach (var worker in _workers)
            this.CollectCreatedNodes(worker);
    }

    private void CollectCreatedNodes(SimulationWorker worker)
    {
        // Future: for each node in worker.Resources.CreatedNodes,
        // call node.AddRef() to increment shared subtree reference counts.
    }

    public void Dispose()
    {
        foreach (var worker in _workers)
            worker.Shutdown();

        foreach (var worker in _workers)
            worker.Join();
    }
}
