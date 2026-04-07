using System.Diagnostics;
using Fabrica.Core.Threading.Queues;

namespace Fabrica.Core.Jobs;

/// <summary>
/// Per-worker internal context owned by <see cref="WorkerPool"/>. Each worker thread has its own
/// instance, providing access to the thread's work-stealing deque, steal offset, and current
/// scheduler. Not exposed to job subclasses — they receive a <see cref="JobContext"/> instead.
///
/// THREAD MODEL
///   <see cref="Enqueue"/> calls <see cref="WorkStealingDeque{T}.Push"/>, which is an owner-only
///   operation. This is safe because <see cref="Enqueue"/> is only called during
///   <see cref="Job.Execute"/>, which runs on the thread that owns this context's deque.
/// </summary>
internal sealed class WorkerContext(WorkerPool pool, int workerIndex)
{
    internal readonly int WorkerIndex = workerIndex;
    internal readonly WorkStealingDeque<Job> Deque = new();

    /// <summary>Round-robin offset for steal target selection. Accessed only by the owning thread.</summary>
    internal int StealOffset;

    /// <summary>
    /// The scheduler that owns the currently executing job's DAG. Set by
    /// <see cref="WorkerPool.ExecuteJob"/> before calling <see cref="Job.Execute"/> and cleared
    /// after. Used by <see cref="Enqueue"/> to stamp sub-jobs with the correct scheduler.
    /// </summary>
    internal JobScheduler? CurrentScheduler;

    /// <summary>
    /// Pushes a ready-to-execute sub-job onto this worker's deque. The sub-job is automatically
    /// stamped with the currently executing job's scheduler, and that scheduler's outstanding count
    /// is incremented.
    /// </summary>
    internal void Enqueue(Job job)
    {
        var scheduler = CurrentScheduler!;

#if DEBUG
        Debug.Assert(job.State == JobState.Pending);
        job.State = JobState.Queued;
#endif
        job.Scheduler = scheduler;
        scheduler.IncrementOutstanding();
        Deque.Push(job);
        pool.NotifyWorkAvailable();
    }
}
