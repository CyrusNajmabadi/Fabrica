namespace Fabrica.Core.Jobs;

/// <summary>
/// Tracks the lifecycle of a <see cref="Job"/> within the scheduler. Managed by scheduler
/// infrastructure; prevents double-execution when a job serves as a terminal counter holder.
/// </summary>
internal enum JobState : byte
{
    /// <summary>Newly created or freshly rented from pool. Eligible for scheduling.</summary>
    Pending = 0,

    /// <summary>Pushed onto a work-stealing deque, awaiting execution.</summary>
    Queued = 1,

    /// <summary>Currently executing on a worker thread.</summary>
    Executing = 2,

    /// <summary>Execution complete and dependencies propagated.</summary>
    Completed = 3,
}

/// <summary>
/// Abstract base class for all jobs in the DAG-based job system. Concrete subclasses carry
/// strongly-typed input/output buffers; the base class owns the scheduling, dependency, and
/// pooling machinery.
///
/// LIFECYCLE
///   1. <b>Rent</b> — The coordinator (or a parent job) rents a job from its type-specific
///      <see cref="JobPool{TJob}"/>. If the pool is empty, a new instance is allocated.
///   2. <b>Configure</b> — The caller sets the job's <see cref="_counter"/> (dependency count),
///      <see cref="_dependents"/> (downstream jobs to decrement), and subclass-specific input fields.
///   3. <b>Submit</b> — The job is pushed onto a <see cref="Collections.WorkStealingDeque{T}"/>
///      for execution via <see cref="JobScheduler.Submit"/> or <see cref="WorkerContext.Enqueue"/>.
///   4. <b>Execute</b> — A worker thread pops or steals the job. The scheduler sets
///      <see cref="_workerContext"/> to the executing thread's context, then calls
///      <see cref="Execute"/>. After execution, the scheduler decrements all
///      <see cref="_dependents"/>' counters, enqueuing any that hit zero.
///   5. <b>Sweep</b> — After the terminal counter completes, the coordinator walks the DAG and
///      calls <see cref="Reset"/> on each job, then returns it to its pool.
///
/// DAG DEPENDENCIES
///   <see cref="_counter"/> gates execution: the scheduler enqueues this job only when
///   <see cref="JobCounter.IsComplete"/> becomes true (via dependency propagation).
///   <see cref="_dependents"/> lists the downstream jobs whose counters this job will decrement
///   upon completion. The coordinator that created the DAG is responsible for wiring these.
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
    /// Dependency counter — this job is eligible to execute only when the counter reaches zero.
    /// Set by the coordinator or parent job during DAG construction. For root jobs (no
    /// dependencies), initialize to <c>new JobCounter(0)</c> or leave at default.
    /// </summary>
    internal JobCounter _counter;

    /// <summary>
    /// Downstream jobs whose counters should be decremented when this job completes. The
    /// scheduler iterates this array after <see cref="Execute"/> returns. May be null if this
    /// job has no dependents (terminal job or standalone).
    /// </summary>
    internal Job[]? _dependents;

    /// <summary>
    /// Intrusive linked-list pointer for <see cref="JobPool{TJob}"/>. Must not be read or
    /// written by derived classes.
    /// </summary>
    internal Job? _poolNext;

    /// <summary>
    /// Per-worker context set by the scheduler immediately before <see cref="Execute"/> and
    /// cleared after it returns. Provides access to the executing thread's deque (for pushing
    /// sub-jobs) and worker index. Must not be accessed outside of <see cref="Execute"/>.
    /// </summary>
    internal WorkerContext? _workerContext;

    /// <summary>
    /// Lifecycle state managed by the scheduler. Prevents double-execution when a job serves as
    /// a terminal counter holder (its counter is decremented after it has already completed).
    /// Reset to <see cref="JobState.Pending"/> when the job is returned to its pool.
    /// </summary>
    internal JobState _state;

    /// <summary>Performs the job's work. Called by a worker thread.</summary>
    internal abstract void Execute();

    /// <summary>
    /// Resets this job's state for pool reuse. Called by the coordinator during the DAG sweep
    /// after the terminal counter completes. Subclasses must clear all input/output buffer
    /// references and any other mutable state.
    /// </summary>
    internal abstract void Reset();
}
