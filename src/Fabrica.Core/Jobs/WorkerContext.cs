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

        // Stamp the sub-job with the scheduler that owns the currently executing DAG. This lets
        // WorkerPool route the job's completion signal (DecrementOutstanding) back to the correct
        // scheduler, which is critical when multiple schedulers share one pool.
        job.Scheduler = scheduler;

        // Tell the scheduler about the new in-flight job *before* making it visible to workers.
        // This ordering is essential: if we pushed first and a worker executed+completed the job
        // before we incremented, the scheduler could see outstanding == 0 prematurely and signal
        // completion while work is still running.
        scheduler.IncrementOutstanding();

        // Push onto this worker's deque (LIFO end). The owning thread is most likely to pop it
        // back (cache-hot), but other idle workers can steal it (FIFO end) for load balancing.
        Deque.Push(job);

        // Wake any parked workers so they can steal the newly available work.
        pool.NotifyWorkAvailable();
    }
}
