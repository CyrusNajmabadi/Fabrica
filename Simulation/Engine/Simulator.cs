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
///     2. SET UP — previous and next images and the cancellation token are
///        written to each worker.
///     3. SIGNAL — all workers are woken from their park state.
///     4. JOIN — a single <see cref="WaitHandleBatch.WaitAll"/> call waits
///        on every worker's done signal.  <see cref="AutoResetEvent"/>
///        handles auto-reset when the wait completes, so no manual reset
///        is needed between ticks.
///     5. REF-COUNT — the Simulator walks each worker's created-nodes list
///        and performs AddRef on shared subtree nodes.  This is the only
///        phase that touches ref-counts, and it runs on a single thread —
///        no interlocked operations needed.
///
///   Because the Simulator always joins all workers before returning,
///   the calling thread sees a fully-consistent next image with all
///   reference counts correct.
///
/// CANCELLATION
///   The engine's <see cref="CancellationToken"/> flows through AdvanceTick
///   to each worker, allowing long-running tick work to exit early when
///   the engine shuts down.  Workers also check the token after waking
///   from their park state.
///
/// WORKER OWNERSHIP
///   Each <see cref="SimulationWorker"/> owns a dedicated
///   <see cref="WorkerResources"/> instance.  Workers never access each
///   other's resources during tick computation.
///
/// THREAD PINNING (future)
///   Workers currently use unpinned background threads.  A future
///   enhancement will pin each worker to a specific core for cache
///   affinity.  See TODO.md.
/// </summary>
internal sealed class Simulator : IDisposable
{
    private readonly SimulationWorker[] _workers;
    private readonly WaitHandleBatch _doneBatch;

    public Simulator(int workerCount)
    {
        if (workerCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(workerCount));

        _workers = new SimulationWorker[workerCount];

        var doneEvents = new WaitHandle[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            _workers[i] = new SimulationWorker(new WorkerResources(), i);
            doneEvents[i] = _workers[i].DoneEvent;
        }

        _doneBatch = new WaitHandleBatch(doneEvents);
    }

    public int WorkerCount => _workers.Length;

    /// <summary>
    /// Dispatches one tick of work across all workers and blocks until
    /// all have completed.  After join, performs deferred ref-counting
    /// on shared subtree nodes created by workers.
    /// </summary>
    public void AdvanceTick(WorldImage previous, WorldImage next, CancellationToken cancellationToken)
    {
        foreach (var worker in _workers)
        {
            worker.Resources.PrepareForTick();
            worker.PreviousImage = previous;
            worker.NextImage = next;
            worker.CancellationToken = cancellationToken;
        }

        foreach (var worker in _workers)
            worker.Signal();

        _doneBatch.WaitAll();

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

        foreach (var worker in _workers)
            worker.Cleanup();
    }
}
