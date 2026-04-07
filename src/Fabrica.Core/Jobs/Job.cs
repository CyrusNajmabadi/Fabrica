using Fabrica.Core.Memory;

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
    internal int RemainingDependencies; // AddDependent/DependsOn; scheduler decrements atomically when a prerequisite completes.

    /// <summary>
    /// Downstream jobs notified on completion.
    /// </summary>
    internal UnsafeList<Job>? Dependents;

    // Owning scheduler for this job's DAG: set by JobScheduler.Submit for root jobs and by WorkerPool for
    // sub-jobs and propagated dependents; read by WorkerPool to route completion signals; cleared by
    // JobPool<TJob>.Return when the job is recycled.
    internal JobScheduler? Scheduler;

    /// <summary>
    /// Intrusive linked-list pointer for <see cref="JobPool{TJob}"/>. Must not be read or
    /// written by derived classes.
    /// </summary>
    internal Job? PoolNext;

#if DEBUG
    internal JobState State;
#endif

    // ── Dependency wiring ───────────────────────────────────────────────

    /// <summary>
    /// Registers <paramref name="dependent"/> to run after this job completes. Increments the
    /// dependent's remaining-dependency count so the scheduler knows to wait.
    /// </summary>
    protected internal void AddDependent(Job dependent)
    {
        // Each job tracks downstream dependents; RemainingDependencies gates execution. The scheduler decrements
        // counts when prerequisites complete; the thread that brings a dependent to zero enqueues it.
        Dependents ??= new UnsafeList<Job>(4);
        Dependents.Add(dependent);
        dependent.RemainingDependencies++;
    }

    /// <summary>
    /// Declares that this job depends on <paramref name="prerequisite"/>: this job will not
    /// execute until <paramref name="prerequisite"/> completes. Adds this job to the prerequisite's
    /// dependents list and increments this job's remaining-dependency count.
    /// </summary>
    protected internal void DependsOn(Job prerequisite)
    {
        // Mirrors AddDependent: prerequisite lists this as a dependent; this job's RemainingDependencies counts
        // outstanding prerequisites.
        prerequisite.Dependents ??= new UnsafeList<Job>(4);
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
    /// Clears state for pool reuse; subclasses must call <c>base.Reset()</c> and clear all input/output
    /// buffer references and any other mutable state.
    /// </summary>
    protected internal virtual void Reset()
    {
        // Called by the coordinator during the DAG sweep after the scheduler's outstanding job count reaches zero.
        // Dependents was lazily allocated: Reset clears the count but retains the backing array for reuse across
        // JobPool cycles.
        Dependents?.Reset();
        RemainingDependencies = 0;
    }

    // FUTURE: sub-job enqueuing from Execute could use a protected helper (e.g. EnqueueChild(Job)) routing through
    // JobContext.WorkerContext without exposing scheduling internals to derived classes.
}
