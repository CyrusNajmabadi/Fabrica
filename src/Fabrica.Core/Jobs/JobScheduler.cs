using System.Diagnostics;

namespace Fabrica.Core.Jobs;

/// <summary>
/// Lightweight per-DAG completion tracker. Multiple schedulers share a single
/// <see cref="WorkerPool"/>, allowing independent DAGs (e.g. simulation and rendering) to execute
/// concurrently on the same workers.
///
/// USAGE
///   1. Create a <see cref="WorkerPool"/> (shared, long-lived).
///   2. Create a <see cref="JobScheduler"/> per logical pipeline (sim, render).
///   3. Call <see cref="Submit"/> to inject root jobs into the pool's shared injection queue.
///   4. Call <see cref="WaitForCompletion"/> to park until the entire DAG drains.
///   5. Repeat from step 3 for the next batch.
///
/// ONE DAG AT A TIME
///   Each scheduler handles one DAG at a time. Submitting while a DAG is in flight is a programming
///   error (caught by a debug assert). This simplifies completion detection: when the outstanding
///   counter reaches zero, the DAG is done.
/// </summary>
internal sealed class JobScheduler
{
    private readonly WorkerPool _pool;

    /// <summary>
    /// Number of jobs that have been enqueued but not yet completed. Incremented on every enqueue
    /// (submit, sub-job enqueue, dependency propagation); decremented after each execution
    /// completes. Reaches zero exactly when all DAG work is done.
    /// </summary>
    private int _outstandingJobs;

    /// <summary>
    /// Signaled by the worker that decrements <see cref="_outstandingJobs"/> to zero, unparking
    /// the coordinator in <see cref="WaitForCompletion"/>. Reset at the start of each wait cycle.
    /// </summary>
    private readonly ManualResetEventSlim _completionSignal = new(false);

    internal JobScheduler(WorkerPool pool) => _pool = pool;

    // ── Coordinator API ─────────────────────────────────────────────────────

    /// <summary>
    /// Submits a ready-to-execute job into the pool's injection queue. Workers will dequeue it.
    /// Must not be called while a previous DAG is still in flight (debug assert).
    /// </summary>
    internal void Submit(Job job)
    {
#if DEBUG
        Debug.Assert(job._state == JobState.Pending);
        job._state = JobState.Queued;
#endif
        job._scheduler = this;
        Interlocked.Increment(ref _outstandingJobs);
        _pool.Inject(job);
    }

    /// <summary>
    /// Parks the coordinator thread until all outstanding jobs have completed. Returns <c>true</c>
    /// if completed, <c>false</c> if the timeout expired. Pass <c>-1</c> for no timeout.
    /// </summary>
    internal bool WaitForCompletion(int millisecondsTimeout = -1)
    {
        if (Volatile.Read(ref _outstandingJobs) == 0)
            return true;

        _completionSignal.Reset();

        if (Volatile.Read(ref _outstandingJobs) == 0)
            return true;

        if (millisecondsTimeout >= 0)
            return _completionSignal.Wait(millisecondsTimeout);

        _completionSignal.Wait();
        return true;
    }

    // ── Called by WorkerPool execution infrastructure ────────────────────────

    /// <summary>
    /// Atomically increments the outstanding job count. Called by <see cref="WorkerPool"/> when a
    /// sub-job is enqueued or a dependent becomes ready.
    /// </summary>
    internal void IncrementOutstanding() => Interlocked.Increment(ref _outstandingJobs);

    /// <summary>
    /// Atomically decrements the outstanding job count. If it reaches zero, signals
    /// <see cref="_completionSignal"/> to unpark the coordinator. Called by <see cref="WorkerPool"/>
    /// after a job finishes execution.
    /// </summary>
    internal void DecrementOutstanding()
    {
        if (Interlocked.Decrement(ref _outstandingJobs) == 0)
            _completionSignal.Set();
    }

    // ── Test accessor ───────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor(JobScheduler scheduler)
    {
        public int OutstandingJobs => Volatile.Read(ref scheduler._outstandingJobs);
    }
}
