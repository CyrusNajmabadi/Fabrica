using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// A long-lived simulation worker thread that parks between ticks and
/// wakes on signal to perform its portion of the tick computation.
///
/// THREADING MODEL
///   Each worker runs a dedicated background thread in a park/signal loop:
///
///     1. Thread parks on its go signal (<see cref="AutoResetEvent"/>),
///        consuming no CPU while idle.
///     2. The <see cref="Simulator"/> sets up the worker's input data
///        (previous image, next image, cancellation token), prepares
///        resources, and calls <see cref="Signal"/>.
///     3. The worker wakes (the go signal auto-resets), performs its
///        computation using its own <see cref="Resources"/>, then sets
///        its done signal.
///     4. The Simulator waits on all done signals via
///        <see cref="WaitHandleBatch"/>.
///     5. The worker loops back to step 1.
///
///   Both signals are <see cref="AutoResetEvent"/>: the go signal
///   auto-resets when the worker wakes, and the done signals auto-reset
///   when the Simulator's <see cref="WaitHandle.WaitAll"/> returns.
///   No manual Reset calls are needed.
///
/// CANCELLATION
///   The engine's <see cref="CancellationToken"/> is written to each worker
///   by the Simulator before every tick dispatch.  Workers check it after
///   waking and pass it through to <see cref="ExecuteTick"/> so that
///   long-running tick work can exit early when the engine shuts down.
///
/// OWNERSHIP
///   Each worker exclusively owns a <see cref="WorkerResources"/> instance.
///   During tick execution, the worker may only access its own resources
///   and the read-only previous image — no shared mutable state is touched.
///
/// SHUTDOWN
///   The <see cref="Simulator"/> manages worker shutdown: it sets the
///   shutdown flag, unblocks the go signal, and joins the thread.
///   Workers do not implement IDisposable — the Simulator owns their
///   lifecycle.
/// </summary>
internal sealed class SimulationWorker
{
    private readonly Thread _thread;
    private readonly AutoResetEvent _goSignal = new(false);
    private readonly AutoResetEvent _doneSignal = new(false);
    private volatile bool _shutdown;

    internal WorkerResources Resources { get; }

    internal AutoResetEvent DoneEvent => _doneSignal;

    // Written by the Simulator before each Signal(); read by the worker
    // thread during ExecuteTick().  No synchronisation needed beyond the
    // signal pair (set-before-signal / read-after-wait).
    internal WorldImage? PreviousImage { get; set; }
    internal WorldImage? NextImage { get; set; }
    internal CancellationToken CancellationToken { get; set; }

    public SimulationWorker(WorkerResources resources, int workerIndex)
    {
        this.Resources = resources;
        _thread = new Thread(this.ThreadLoop)
        {
            Name = $"SimWorker-{workerIndex}",
            IsBackground = true,
        };
        _thread.Start();
    }

    private void ThreadLoop()
    {
        while (true)
        {
            _goSignal.WaitOne();
            if (_shutdown || this.CancellationToken.IsCancellationRequested)
                return;

            this.ExecuteTick();

            _doneSignal.Set();
        }
    }

    private void ExecuteTick()
    {
        // TODO: actual per-worker tick computation.
        // Read PreviousImage (immutable), write into NextImage partition,
        // log new shared nodes into Resources.CreatedNodes.
        // Check CancellationToken for early exit during long work.
    }

    /// <summary>
    /// Wakes the worker thread to begin its next tick.
    /// The go signal auto-resets when the worker wakes — no manual
    /// reset is needed.
    /// </summary>
    internal void Signal() =>
        _goSignal.Set();

    /// <summary>
    /// Sets the shutdown flag and unblocks the thread so it can exit.
    /// Called by <see cref="Simulator.Dispose"/> — must be followed by
    /// <see cref="Join"/> to ensure the thread has terminated.
    /// </summary>
    internal void Shutdown()
    {
        _shutdown = true;
        _goSignal.Set();
    }

    /// <summary>
    /// Blocks until the worker thread has exited.
    /// </summary>
    internal void Join() =>
        _thread.Join();

    /// <summary>
    /// Disposes OS handles for the go and done signals.
    /// Called by <see cref="Simulator.Dispose"/> after the thread has
    /// been joined and no further waits will occur.
    /// </summary>
    internal void Cleanup()
    {
        _goSignal.Dispose();
        _doneSignal.Dispose();
    }
}
