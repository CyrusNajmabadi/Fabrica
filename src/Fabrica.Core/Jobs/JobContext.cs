namespace Fabrica.Core.Jobs;

/// <summary>
/// Minimal execution context passed to <see cref="Job.Execute"/>. Exposes only what downstream
/// job subclasses need — currently just the worker identity for indexing into per-worker buffers.
///
/// Internally holds the full <see cref="WorkerContext"/> so that future protected helpers on
/// <see cref="Job"/> (e.g. sub-job enqueuing) can route through the scheduler and deque without
/// changing the <see cref="Job.Execute"/> signature.
/// </summary>
public readonly struct JobContext
{
    /// <summary>
    /// Zero-based index of the worker thread executing this job. Use this to index into
    /// per-worker arrays (e.g. <c>threadLocalBuffers[context.WorkerIndex]</c>).
    /// </summary>
    public readonly int WorkerIndex;

    internal readonly WorkerContext WorkerContext;

    internal JobContext(WorkerContext workerContext)
    {
        WorkerIndex = workerContext.WorkerIndex;
        WorkerContext = workerContext;
    }
}
