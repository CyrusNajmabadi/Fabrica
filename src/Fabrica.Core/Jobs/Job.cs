namespace Fabrica.Core.Jobs;

/// <summary>
/// Abstract base class for all jobs in the DAG-based job system. Concrete subclasses carry
/// strongly-typed input/output buffers; the base class owns the scheduling, dependency, and
/// pooling machinery.
///
/// LIFECYCLE
///   1. <b>Rent</b> — The coordinator (or a parent job) rents a job from its type-specific
///      <see cref="JobPool{TJob}"/>. If the pool is empty, a new instance is allocated.
///   2. <b>Configure</b> — The caller sets the job's <see cref="Counter"/> (dependency count),
///      <see cref="Dependents"/> (downstream counters to decrement), and subclass-specific
///      input fields.
///   3. <b>Submit</b> — The job is pushed onto a <see cref="Collections.WorkStealingDeque{T}"/>
///      for execution.
///   4. <b>Execute</b> — A worker thread pops or steals the job. The scheduler sets
///      <see cref="WorkerContext"/> to the executing thread's context, then calls
///      <see cref="Execute"/>. After execution, the scheduler decrements all
///      <see cref="Dependents"/>' counters, enqueuing any that hit zero.
///   5. <b>Sweep</b> — After the terminal counter completes, the coordinator walks the DAG and
///      calls <see cref="Reset"/> on each job, then returns it to its pool.
///
/// DAG DEPENDENCIES
///   <see cref="Counter"/> gates execution: the scheduler only runs this job when
///   <see cref="JobCounter.IsComplete"/> is true. <see cref="Dependents"/> lists the downstream
///   jobs whose counters this job will decrement upon completion. The coordinator that created
///   the DAG is responsible for wiring these references.
///
/// POOLING
///   <see cref="PoolNext"/> is the intrusive linked-list pointer used by
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

    /// <summary>Performs the job's work. Called by a worker thread.</summary>
    internal abstract void Execute();

    /// <summary>
    /// Resets this job's state for pool reuse. Called by the coordinator during the DAG sweep
    /// after the terminal counter completes. Subclasses must clear all input/output buffer
    /// references and any other mutable state.
    /// </summary>
    internal abstract void Reset();
}
