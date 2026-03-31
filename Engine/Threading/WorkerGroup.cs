namespace Engine.Threading;

/// <summary>
/// Coordinates N <see cref="ThreadWorker{TState,TExecutor}"/> instances through
/// a parallel dispatch cycle: prepare → set state → signal all → wait all.
///
/// DISPATCH CYCLE (<see cref="Dispatch"/>)
///   1. PREPARE — each worker's executor has <see cref="IThreadExecutor{TState}.Prepare"/>
///      called to clear per-dispatch accumulation state.
///   2. SET UP — the shared <typeparamref name="TState"/> and cancellation token
///      are written to each worker.
///   3. SIGNAL — all workers are woken from their park state.
///   4. JOIN — a single <see cref="WaitHandleBatch.WaitAll"/> call waits on every
///      worker's done signal.  <see cref="AutoResetEvent"/> handles auto-reset
///      when the wait completes, so no manual reset is needed between dispatches.
///
/// After <see cref="Dispatch"/> returns, the coordinator can walk
/// <see cref="Workers"/> to perform post-join work (e.g. collecting created nodes
/// from simulation executors, gathering render results).
///
/// THREAD PINNING
///   Workers are pinned to logical cores starting at <c>coreIndexOffset</c>.
///   This lets simulation workers pin to cores 0..N-1 and render workers pin to
///   cores N..N+M-1, avoiding overlap.
///
/// OWNERSHIP
///   The group owns the workers and their threads.  <see cref="Dispose"/> shuts
///   down all workers, joins their threads, and releases OS handles.
/// </summary>
internal sealed class WorkerGroup<TState, TExecutor> : IDisposable
    where TState : struct
    where TExecutor : struct, IThreadExecutor<TState>
{
    private readonly ThreadWorker<TState, TExecutor>[] _workers;
    private readonly WaitHandleBatch _doneBatch;

    public WorkerGroup(int workerCount, Func<int, TExecutor> executorFactory, string threadNamePrefix, int coreIndexOffset = 0)
    {
        if (workerCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(workerCount));

        _workers = new ThreadWorker<TState, TExecutor>[workerCount];

        var doneEvents = new WaitHandle[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            var executor = executorFactory(i);
            _workers[i] = new ThreadWorker<TState, TExecutor>(
                executor,
                coreIndexOffset + i,
                $"{threadNamePrefix}-{i}");
            doneEvents[i] = _workers[i].DoneEvent;
        }

        _doneBatch = new WaitHandleBatch(doneEvents);
    }

    public int WorkerCount => _workers.Length;

    /// <summary>
    /// Direct access to workers for post-join inspection of executor state
    /// (e.g. reading created-nodes lists, render results).
    /// </summary>
    public ReadOnlySpan<ThreadWorker<TState, TExecutor>> Workers => _workers;

    /// <summary>
    /// Dispatches one unit of work across all workers and blocks until all
    /// have completed.  The same <paramref name="state"/> is set on every
    /// worker — workers distinguish their partition by executor-internal
    /// state (e.g. worker index stored in the executor).
    /// </summary>
    public void Dispatch(TState state, CancellationToken cancellationToken)
    {
        foreach (var worker in _workers)
        {
            worker.Executor.Prepare();
            worker.State = state;
            worker.CancellationToken = cancellationToken;
        }

        foreach (var worker in _workers)
            worker.Signal();

        _doneBatch.WaitAll();
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
