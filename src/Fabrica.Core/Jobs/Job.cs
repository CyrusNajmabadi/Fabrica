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
///   2. <b>Configure</b> — The caller sets <see cref="_remainingDependencies"/>,
///      <see cref="_dependents"/>, and subclass-specific input fields.
///   3. <b>Submit</b> — The job is pushed onto a <see cref="Collections.WorkStealingDeque{T}"/>
///      via <see cref="JobScheduler.Submit"/> or <see cref="WorkerContext.Enqueue"/>.
///   4. <b>Execute</b> — A worker thread pops or steals the job. The scheduler passes the
///      executing thread's <see cref="WorkerContext"/> to <see cref="Execute"/>, giving the job
///      access to the thread's deque for enqueuing sub-jobs. After execution, the scheduler
///      decrements all <see cref="_dependents"/>' dependency counts, enqueuing any that hit zero.
///   5. <b>Sweep</b> — After the scheduler's outstanding job count reaches zero, the coordinator
///      walks the DAG and calls <see cref="Reset"/> on each job, then returns it to its pool.
///
/// DAG DEPENDENCIES
///   <see cref="_remainingDependencies"/> gates execution: the scheduler enqueues this job only
///   when the count reaches zero (via atomic decrement from completed prerequisites).
///   <see cref="_dependents"/> lists the downstream jobs whose counts this job decrements upon
///   completion.
///
/// POOLING
///   <see cref="_poolNext"/> is the intrusive linked-list pointer used by
///   <see cref="JobPool{TJob}"/> for lock-free Treiber stack operations. It must not be read
///   or written by derived classes.
///
/// VIRTUAL DISPATCH
///   <see cref="Execute"/> and <see cref="Reset"/> are virtual calls. At typical job granularity
///   (microseconds to milliseconds of work per job), the ~2ns vtable lookup is negligible.
/// </summary>
internal abstract class Job
{
    /// <summary>
    /// Number of prerequisite jobs that must complete before this job is eligible to run.
    /// Set during DAG construction. The scheduler atomically decrements this when a prerequisite
    /// completes; the thread that brings it to zero enqueues the job. For root jobs (no
    /// dependencies), leave at default (0).
    /// </summary>
    internal int _remainingDependencies;

    /// <summary>
    /// Downstream jobs whose dependency counts should be decremented when this job completes.
    /// The scheduler iterates this array after <see cref="Execute"/> returns. May be null if
    /// this job has no dependents (leaf of the DAG).
    /// </summary>
    internal Job[]? _dependents;

    /// <summary>
    /// Intrusive linked-list pointer for <see cref="JobPool{TJob}"/>. Must not be read or
    /// written by derived classes.
    /// </summary>
    internal Job? _poolNext;

#if DEBUG
    internal JobState _state;
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
