using System.Diagnostics;
using Fabrica.Core.Collections;

namespace Fabrica.Core.Jobs;

/// <summary>
/// Per-worker context passed to jobs during execution. Each worker thread (and the coordinator)
/// has its own context instance, providing access to the thread's work-stealing deque and
/// worker identity.
///
/// Jobs use <see cref="Enqueue"/> to push ready sub-jobs onto the executing thread's deque.
/// The scheduler sets <see cref="Job._workerContext"/> before calling <see cref="Job.Execute"/>
/// and clears it after, ensuring the context is only accessible during execution.
///
/// THREAD MODEL
///   <see cref="Enqueue"/> calls <see cref="WorkStealingDeque{T}.Push"/>, which is an owner-only
///   operation. This is safe because <see cref="Enqueue"/> is only called during
///   <see cref="Job.Execute"/>, which runs on the thread that owns this context's deque.
/// </summary>
internal sealed class WorkerContext(JobScheduler scheduler, int workerIndex)
{
    internal readonly int WorkerIndex = workerIndex;
    internal readonly WorkStealingDeque<Job> Deque = new();

    /// <summary>Round-robin offset for steal target selection. Accessed only by the owning thread.</summary>
    internal int _stealOffset;

    /// <summary>
    /// Pushes a ready-to-execute job onto this worker's deque, increments the scheduler's
    /// outstanding job count, and signals that work is available.
    /// </summary>
    internal void Enqueue(Job job)
    {
        Debug.Assert(job._state == JobState.Pending);
        job._state = JobState.Queued;
        scheduler.IncrementOutstanding();
        Deque.Push(job);
        scheduler.NotifyWorkAvailable();
    }
}
