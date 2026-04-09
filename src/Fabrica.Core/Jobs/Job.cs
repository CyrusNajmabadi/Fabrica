using Fabrica.Core.Collections.Unsafe;

namespace Fabrica.Core.Jobs;

#if DEBUG
internal enum JobState : byte
{
    Pending = 0,
    Queued = 1,
    Executing = 2,
    Completed = 3,
}
#endif

/// <summary>
/// Abstract base class for all jobs in the DAG-based job system. Concrete subclasses carry
/// strongly-typed input/output buffers; the base class owns scheduling, dependency, and pooling
/// machinery.
///
/// <see cref="PoolNext"/> is the intrusive linked-list pointer used by
/// <see cref="JobPool{TJob}"/> for lock-free Treiber stack operations. It must not be read
/// or written by derived classes.
/// </summary>
public abstract class Job
{
    /// <summary>
    /// Prerequisite count for DAG readiness: zero means this job is eligible to run.
    /// </summary>
    internal int RemainingDependencies; // DependsOn increments; scheduler decrements atomically when a prerequisite completes.

    /// <summary>
    /// Downstream jobs notified on completion. Default-initialized (backing array is null);
    /// lazily allocated on first <see cref="DependsOn"/> call.
    /// </summary>
    internal NonCopyableUnsafeList<Job> Dependents;

    // Owning scheduler for this job's DAG: set by JobScheduler.Submit for root jobs and by WorkerPool for
    // sub-jobs and propagated dependents; read by WorkerPool to route completion signals; cleared by
    // JobPool<TJob>.Return when the job is recycled.
    internal JobScheduler? Scheduler;

    /// <summary>
    /// Intrusive linked-list pointer for <see cref="JobPool{TJob}"/>. Must not be read or
    /// written by derived classes.
    /// </summary>
    internal Job? PoolNext;

    /// <summary>
    /// Combined reference-count and should-be-on-freelist flag for <see cref="JobPool{TJob}"/>.
    /// Low 31 bits: reference count (number of threads currently reading this node's PoolNext
    /// during a try_get, plus 1 while the node is on the list). High bit (sign bit):
    /// SHOULD_BE_ON_FREELIST flag, set when a Return wants to add the node but the refcount is
    /// non-zero. Packing both into a single int32 makes updates fully atomic via
    /// <see cref="Interlocked.Add(ref int, int)"/> and
    /// <see cref="Interlocked.CompareExchange(ref int, int, int)"/>, avoiding the race between
    /// refcount reaching zero and the flag being checked separately.
    /// Algorithm: cameron314's ABA-safe lock-free free list (moodycamel/concurrentqueue).
    /// See THIRD-PARTY-NOTICES.md for license details.
    /// </summary>
    internal int FreeListRefs;

#if DEBUG
    internal JobState State;
#endif

    // ── Dependency wiring ───────────────────────────────────────────────

    /// <summary>
    /// Declares that this job depends on <paramref name="prerequisite"/>: this job will not
    /// execute until <paramref name="prerequisite"/> completes.
    /// </summary>
    protected internal void DependsOn(Job prerequisite)
    {
        // The prerequisite tracks this job as a downstream dependent; RemainingDependencies gates execution.
        // The scheduler decrements counts when prerequisites complete; the thread that brings a job to zero
        // enqueues it.
        if (!prerequisite.Dependents.IsInitialized)
            prerequisite.Dependents = new NonCopyableUnsafeList<Job>(4);
        prerequisite.Dependents.Add(this);
        RemainingDependencies++;
    }

    // ── Execution ───────────────────────────────────────────────────────

    /// <summary>
    /// Performs the job's work. Called by a worker thread. The <paramref name="context"/> provides
    /// the executing worker's identity for indexing into per-worker buffers.
    /// </summary>
    // LIFECYCLE (submit): root jobs are pushed onto a work-stealing deque via JobScheduler.Submit.
    // LIFECYCLE (execute): a worker pops or steals the job; the scheduler passes JobContext for per-worker buffer
    // indexing. After Execute returns, the scheduler decrements dependents' RemainingDependencies and enqueues any
    // that reach zero.
    // VIRTUAL DISPATCH: Execute and Reset are virtual; at typical job granularity the ~2ns vtable cost is negligible.
    protected internal abstract void Execute(JobContext context);

    /// <summary>
    /// Resets all base-class and subclass state so the job can be re-wired and resubmitted.
    /// Base-class cleanup (dependency wiring, scheduling state) runs first, then delegates to
    /// <see cref="ResetState"/> for subclass-specific fields.
    /// </summary>
    public void Reset()
    {
        // Called by the coordinator during the DAG sweep after the scheduler's outstanding job count reaches zero.
        // Dependents was lazily allocated: Reset clears the count but retains the backing array for reuse across
        // JobPool cycles. Safe to call on default (uninitialized) struct — Reset just sets _count to 0.
        Dependents.Reset();
        RemainingDependencies = 0;
        this.ResetState();
    }

    /// <summary>
    /// Clears subclass-specific state (input/output buffers, handles, etc.) for pool reuse.
    /// </summary>
    protected abstract void ResetState();

    // FUTURE: sub-job enqueuing from Execute could use a protected helper (e.g. EnqueueChild(Job)) routing through
    // JobContext.WorkerContext without exposing scheduling internals to derived classes.
}
