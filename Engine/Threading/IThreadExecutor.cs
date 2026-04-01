namespace Engine.Threading;

/// <summary>
/// Domain-specific work executed by a <see cref="WorkerGroup{TState,TExecutor}.ThreadWorker"/> on its dedicated thread.
///
/// The struct constraint enables the JIT to specialize each generic instantiation, giving zero-overhead dispatch — no interface
/// vtable indirection on the hot path.
///
/// <typeparamref name="TState"/> is the per-dispatch input data written by the coordinator before signaling the worker (e.g.
/// images for simulation, a render frame for rendering).
/// </summary>
internal interface IThreadExecutor<TState>
    where TState : struct
{
    /// <summary>
    /// Called by the coordinator on its own thread before each dispatch to clear per-dispatch accumulation state (e.g.
    /// created-nodes list, per-frame buffers).
    /// </summary>
    void Prepare();

    /// <summary>
    /// Performs the actual work on the worker thread after it wakes from its park state. <paramref name="state"/> is the input
    /// data set by the coordinator; <paramref name="cancellationToken"/> allows early exit during engine shutdown.
    /// </summary>
    void Execute(in TState state, CancellationToken cancellationToken);
}
