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
/// strongly-typed input/output buffers; the base class owns the scheduling, dependency, and
/// pooling machinery.
///
/// LIFECYCLE
///   1. <b>Rent</b> — The coordinator (or a parent job) rents a job from its type-specific
///      <see cref="JobPool{TJob}"/>. If the pool is empty, a new instance is allocated.
///   2. <b>Configure</b> — The caller sets subclass-specific input fields and wires DAG
///      dependencies via <see cref="AddDependent"/> or <see cref="DependsOn"/>.
///   3. <b>Submit</b> — The job is pushed onto a <see cref="Collections.WorkStealingDeque{T}"/>
///      via <see cref="JobScheduler.Submit"/> or <see cref="WorkerContext.Enqueue"/>.
///   4. <b>Execute</b> — A worker thread pops or steals the job. The scheduler passes the
///      executing thread's <see cref="WorkerContext"/> to <see cref="Execute"/>, giving the job
///      access to the thread's deque for enqueuing sub-jobs. After execution, the scheduler
///      decrements all dependents' dependency counts, enqueuing any that hit zero.
///   5. <b>Sweep</b> — After the scheduler's outstanding job count reaches zero, the coordinator
///      walks the DAG and calls <see cref="Reset"/> on each job, then returns it to its pool.
///
/// DAG DEPENDENCIES
///   Managed through <see cref="AddDependent"/> and <see cref="DependsOn"/>. Internally, each
///   job tracks its downstream dependents and a remaining-dependency count that gates execution.
///   The scheduler atomically decrements the count when prerequisites complete; the thread that
///   brings it to zero enqueues the job.
///
/// POOLING
///   <see cref="PoolNext"/> is the intrusive linked-list pointer used by
///   <see cref="JobPool{TJob}"/> for lock-free Treiber stack operations. It must not be read
///   or written by derived classes.
///
/// VIRTUAL DISPATCH
///   <see cref="Execute"/> and <see cref="Reset"/> are virtual calls. At typical job granularity
///   (microseconds to milliseconds of work per job), the ~2ns vtable lookup is negligible.
///
/// FUTURE: SIMPLIFIED PUBLIC API
///   Higher layers should see only <see cref="JobScheduler.Submit"/> for the root job. Child jobs
///   created during <see cref="Execute"/> should be enqueued through a protected helper on this
///   base class (e.g. <c>EnqueueChild(Job)</c>) rather than reaching through
///   <see cref="WorkerContext"/>. This is possible because <see cref="Scheduler"/> is available on
///   the base type for the duration of <see cref="Execute"/>. The goal is that derived job classes
///   never interact with <see cref="WorkerContext"/> or scheduling internals directly.
/// </summary>
public abstract class Job
{
    /// <summary>
    /// Number of prerequisite jobs that must complete before this job is eligible to run.
    /// Managed by <see cref="AddDependent"/>/<see cref="DependsOn"/>; decremented atomically
    /// by the scheduler when a prerequisite completes. Zero means ready to execute.
    /// </summary>
    internal int RemainingDependencies;

    /// <summary>
    /// Downstream jobs whose dependency counts should be decremented when this job completes.
    /// Managed by <see cref="AddDependent"/>/<see cref="DependsOn"/>; iterated by the scheduler
    /// after <see cref="Execute"/> returns. Lazily allocated on first <see cref="AddDependent"/>
    /// call and reused across pool cycles via <see cref="UnsafeList{T}.Reset"/>.
    /// </summary>
    internal UnsafeList<Job>? Dependents;

    /// <summary>
    /// The <see cref="JobScheduler"/> that owns this job's DAG. Set by <see cref="JobScheduler.Submit"/>
    /// for root jobs and by <see cref="WorkerPool"/> for sub-jobs and propagated dependents. Read by
    /// <see cref="WorkerPool"/> to route completion signals to the correct scheduler. Cleared by
    /// <see cref="JobPool{TJob}.Return"/> when the job is recycled.
    /// </summary>
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
    /// dependent's remaining-dependency count so the scheduler knows to wait. Call from within
    /// a derived class: <c>this.AddDependent(downstream)</c>.
    /// </summary>
    protected void AddDependent(Job dependent)
    {
        Dependents ??= new UnsafeList<Job>(4);
        Dependents.Add(dependent);
        dependent.RemainingDependencies++;
    }

    /// <summary>
    /// Declares that this job depends on <paramref name="prerequisite"/>: this job will not
    /// execute until <paramref name="prerequisite"/> completes. Adds this job to the prerequisite's
    /// dependents list and increments this job's remaining-dependency count. Call from within
    /// a derived class: <c>this.DependsOn(upstream)</c>.
    /// </summary>
    protected void DependsOn(Job prerequisite)
    {
        prerequisite.Dependents ??= new UnsafeList<Job>(4);
        prerequisite.Dependents.Add(this);
        RemainingDependencies++;
    }

    // ── Execution ───────────────────────────────────────────────────────

    /// <summary>
    /// Performs the job's work. Called by a worker thread. The <paramref name="context"/> provides
    /// access to the executing thread's deque (for enqueuing sub-jobs via
    /// <see cref="WorkerContext.Enqueue"/>) and worker index.
    /// </summary>
    protected internal abstract void Execute(WorkerContext context);

    /// <summary>
    /// Resets this job's state for pool reuse. Called by the coordinator during the DAG sweep
    /// after the scheduler's outstanding count reaches zero. Subclasses must call
    /// <c>base.Reset()</c> and clear all input/output buffer references and any other mutable state.
    /// </summary>
    protected internal virtual void Reset()
    {
        Dependents?.Reset();
        RemainingDependencies = 0;
    }
}
