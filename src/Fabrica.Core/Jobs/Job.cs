namespace Fabrica.Core.Jobs;

/// <summary>
/// Base class for all poolable jobs in the work-stealing job system.
///
/// LIFECYCLE
///   1. Rent — The coordinator (or any job-spawning thread) rents a job from its type-specific pool via
///      <see cref="JobPool{TJob, TAllocator}.Rent"/>. If the pool is empty, a new instance is allocated via the
///      <see cref="Memory.IAllocator{T}"/>.
///
///   2. Configure — The caller sets job-specific fields (indices, references, etc.) on the rented instance.
///
///   3. Submit — The job is pushed onto a <see cref="Collections.WorkStealingDeque{T}"/> for execution.
///
///   4. Execute — A worker thread pops or steals the job and calls <see cref="Execute"/>. This is the job's main work.
///
///   5. Return — After execution, the worker calls <see cref="Return"/>. The derived class delegates to its pool's
///      <c>Return</c> method, which resets the job's state via the allocator and pushes it back into the pool for
///      reuse.
///
/// POOLING
///   Each concrete job type manages its own <see cref="JobPool{TJob, TAllocator}"/>. The pool is shared across all
///   threads — the thread that rents and the thread that returns may differ.
///
/// VIRTUAL DISPATCH
///   <see cref="Execute"/> and <see cref="Return"/> are virtual calls. At typical job granularity (microseconds to
///   milliseconds of work per job), the ~2ns vtable lookup is negligible.
/// </summary>
public abstract class Job
{
    /// <summary>
    /// Intrusive linked-list pointer used by <see cref="JobPool{TJob, TAllocator}"/> for lock-free stack operations.
    /// Must not be read or written by derived classes.
    /// </summary>
    internal Job? _poolNext;

    /// <summary>Performs the job's work. Called by a worker thread.</summary>
    public abstract void Execute();

    /// <summary>
    /// Returns this job to its type-specific pool for reuse. Called by the worker thread after <see cref="Execute"/>
    /// completes. Derived classes override this to call their pool's <c>Return</c> method, which handles reset via
    /// the allocator.
    /// </summary>
    public abstract void Return();
}
