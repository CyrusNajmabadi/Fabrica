namespace Fabrica.Core.Jobs;

/// <summary>
/// Minimal execution context passed to <see cref="Job.Execute"/>. Exposes only what downstream
/// job subclasses need — currently just the worker identity for indexing into per-worker buffers.
/// </summary>
public readonly struct JobContext
{
    /// <summary>
    /// Zero-based index of the worker thread executing this job. Use this to index into
    /// per-worker arrays (e.g. <c>threadLocalBuffers[context.WorkerIndex]</c>).
    /// </summary>
    public int WorkerIndex => WorkerContext.WorkerIndex;

    // Holds the full WorkerContext so future protected helpers on Job (e.g. sub-job enqueuing) can route through
    // the scheduler and deque without changing the Job.Execute signature.
    internal readonly WorkerContext WorkerContext;

    internal JobContext(WorkerContext workerContext) =>
        WorkerContext = workerContext;
}
