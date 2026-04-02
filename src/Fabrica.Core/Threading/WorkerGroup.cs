namespace Fabrica.Core.Threading;

/// <summary>
/// Coordinates N <see cref="WorkerGroup{TState,TExecutor}.ThreadWorker"/> instances through a parallel dispatch cycle: prepare →
/// set state → signal all → wait all.
///
/// DISPATCH CYCLE (<see cref="Dispatch"/>)
///   1. PREPARE — each worker's executor has <see cref="IThreadExecutor{TState}.Prepare"/>
///      called to clear per-dispatch accumulation state.
///   2. SET UP — the shared <typeparamref name="TState"/> and cancellation token
///      are written to each worker.
///   3. SIGNAL — all workers are woken from their park state.
///   4. JOIN — a single <see cref="WorkerGroup{TState,TExecutor}.WaitHandleBatch.WaitAll"/> call waits on every
///      worker's done signal. <see cref="AutoResetEvent"/> handles auto-reset when the wait completes, so no manual reset is
///      needed between dispatches.
///
/// After <see cref="Dispatch"/> returns, the coordinator can walk <see cref="Workers"/> to perform post-join work (e.g.
/// collecting created nodes from simulation executors, gathering render results).
///
/// THREAD PINNING
///   Workers are pinned to logical cores 0..N-1 at thread startup. This is best-effort — pinning may fail silently on restricted
///   kernels, containerised environments, or macOS.
///
/// OWNERSHIP
///   The group owns the workers and their threads.  <see cref="Shutdown"/> signals
///   all workers to exit and blocks until their threads have terminated.
/// </summary>
public sealed partial class WorkerGroup<TState, TExecutor>
    where TState : struct
    where TExecutor : struct, IThreadExecutor<TState>
{
    private readonly ThreadWorker[] _workers;
    private readonly WaitHandleBatch _doneBatch;

    public WorkerGroup(int workerCount, Func<int, TExecutor> executorFactory, string threadNamePrefix)
    {
        if (workerCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(workerCount));

        _workers = new ThreadWorker[workerCount];

        var doneEvents = new WaitHandle[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            var executor = executorFactory(i);
            _workers[i] = new ThreadWorker(
                executor,
                i,
                $"{threadNamePrefix}-{i}");
            doneEvents[i] = _workers[i].DoneEvent;
        }

        _doneBatch = new WaitHandleBatch(doneEvents);
    }

    public int WorkerCount => _workers.Length;

    /// <summary>
    /// Direct access to workers for post-join inspection of executor state (e.g. reading created-nodes lists, render results).
    /// Internal: <see cref="ThreadWorker"/> is not public; trusted assemblies use <c>InternalsVisibleTo</c>.
    /// </summary>
    internal ReadOnlySpan<ThreadWorker> Workers => _workers;

    /// <summary>
    /// Dispatches one unit of work across all workers and blocks until all have completed. The same <paramref name="state"/> is
    /// set on every worker — workers distinguish their partition by executor-internal state (e.g. worker index stored in the
    /// executor).
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

    public void Shutdown()
    {
        foreach (var worker in _workers)
            worker.Shutdown();

        foreach (var worker in _workers)
            worker.Join();
    }
}
