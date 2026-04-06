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
///   2. <b>Configure</b> — The caller sets <see cref="RemainingDependencies"/>,
///      <see cref="Dependents"/>, and subclass-specific input fields.
///   3. <b>Submit</b> — The job is pushed onto a <see cref="Collections.WorkStealingDeque{T}"/>
///      via <see cref="JobScheduler.Submit"/> or <see cref="WorkerContext.Enqueue"/>.
///   4. <b>Execute</b> — A worker thread pops or steals the job. The scheduler passes the
///      executing thread's <see cref="WorkerContext"/> to <see cref="Execute"/>, giving the job
///      access to the thread's deque for enqueuing sub-jobs. After execution, the scheduler
///      decrements all <see cref="Dependents"/>' dependency counts, enqueuing any that hit zero.
///   5. <b>Sweep</b> — After the scheduler's outstanding job count reaches zero, the coordinator
///      walks the DAG and calls <see cref="Reset"/> on each job, then returns it to its pool.
///
/// DAG DEPENDENCIES
///   <see cref="RemainingDependencies"/> gates execution: the scheduler enqueues this job only
///   when the count reaches zero (via atomic decrement from completed prerequisites).
///   <see cref="Dependents"/> lists the downstream jobs whose counts this job decrements upon
///   completion.
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
internal abstract class Job
{
    /// <summary>
    /// Number of prerequisite jobs that must complete before this job is eligible to run.
    /// Set during DAG construction. The scheduler atomically decrements this when a prerequisite
    /// completes; the thread that brings it to zero enqueues the job. For root jobs (no
    /// dependencies), leave at default (0).
    /// </summary>
    internal int RemainingDependencies;

    /// <summary>
    /// Downstream jobs whose dependency counts should be decremented when this job completes.
    /// The scheduler iterates this array after <see cref="Execute"/> returns. May be null if
    /// this job has no dependents (leaf of the DAG).
    /// </summary>
    internal Job[]? Dependents;

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

    /// <summary>
    /// Performs the job's work. Called by a worker thread. The <paramref name="context"/> provides
    /// access to the executing thread's deque (for enqueuing sub-jobs via
    /// <see cref="WorkerContext.Enqueue"/>) and worker index.
    /// </summary>
    internal abstract void Execute(WorkerContext context);

    /// <summary>
    /// Resets this job's state for pool reuse. Called by the coordinator during the DAG sweep
    /// after the scheduler's outstanding count reaches zero. Subclasses must clear all
    /// input/output buffer references and any other mutable state.
    /// </summary>
    internal abstract void Reset();
}
