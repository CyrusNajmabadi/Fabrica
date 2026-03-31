namespace Simulation.Engine;

/// <summary>
/// Parallel coordinator for N render workers.
///
/// FRAME DISPATCH CYCLE
///   <see cref="DispatchFrame"/> runs once per consumption-loop frame, called
///   by <see cref="SimulationConsumer{TRenderer}"/>:
///
///     1. PREPARE — each worker's <see cref="RenderExecutor.Prepare"/>
///        clears per-frame state via <see cref="RenderWorkerResources"/>.
///     2. SET UP — the <see cref="RenderFrame"/> and cancellation token are
///        written to each worker as a <see cref="RenderDispatchState"/>.
///     3. SIGNAL — all workers are woken from their park state.
///     4. JOIN — <see cref="WorkerGroup{TState,TExecutor}.Dispatch"/> waits
///        on every worker's done signal.
///
///   All workers must finish before <see cref="DispatchFrame"/> returns,
///   because the consumption loop advances the epoch immediately afterward
///   and the simulation may then reclaim the snapshots.
///
/// THREAD PINNING
///   Render workers are pinned starting at <c>coreIndexOffset</c> so they
///   do not overlap with simulation workers (which pin to cores 0..N-1).
/// </summary>
internal sealed class RenderCoordinator : IDisposable
{
    private readonly WorkerGroup<RenderDispatchState, RenderExecutor> _group;

    public RenderCoordinator(int workerCount, int coreIndexOffset = 0) =>
        _group = new WorkerGroup<RenderDispatchState, RenderExecutor>(
            workerCount,
            static i => new RenderExecutor(new RenderWorkerResources()),
            "RenderWorker",
            coreIndexOffset);

    public int WorkerCount => _group.WorkerCount;

    /// <summary>
    /// Dispatches one frame of render work across all workers and blocks
    /// until all have completed.
    /// </summary>
    public void DispatchFrame(in RenderFrame frame, CancellationToken cancellationToken)
    {
        var state = new RenderDispatchState { Frame = frame };
        _group.Dispatch(state, cancellationToken);
    }

    public void Dispose() =>
        _group.Dispose();
}
