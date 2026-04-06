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
///   3. Call <see cref="Submit"/> with a root job. The call blocks until the entire DAG drains.
///   4. Repeat from step 3 for the next batch.
///
/// ONE DAG AT A TIME
///   <see cref="Submit"/> blocks until the DAG completes, so only one DAG is in flight at a time.
/// </summary>
public sealed class JobScheduler(WorkerPool pool)
{
    /// <summary>
    /// Number of jobs that have been enqueued but not yet completed. Incremented on every enqueue
    /// (submit, sub-job enqueue, dependency propagation); decremented after each execution
    /// completes. Reaches zero exactly when all DAG work is done.
    /// </summary>
    private int _outstandingJobs;

    /// <summary>
    /// Signaled by the worker that decrements <see cref="_outstandingJobs"/> to zero, unparking
    /// the coordinator in <see cref="Submit"/>. Reset at the start of each wait cycle.
    /// </summary>
    private readonly ManualResetEventSlim _completionSignal = new(false);

    // ── Coordinator API ─────────────────────────────────────────────────────

    /// <summary>
    /// Submits a root job and blocks until the entire DAG (including sub-jobs and dependents)
    /// completes. Returns <c>true</c> if completed, <c>false</c> if the timeout expired. Pass
    /// <c>-1</c> for no timeout.
    /// </summary>
    public bool Submit(Job job, int millisecondsTimeout = -1)
    {
        this.InjectJob(job);
        return this.WaitForCompletion(millisecondsTimeout);
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

    // ── Private ─────────────────────────────────────────────────────────────

    private void InjectJob(Job job)
    {
#if DEBUG
        Debug.Assert(job.State == JobState.Pending);
        job.State = JobState.Queued;
#endif
        job.Scheduler = this;
        Interlocked.Increment(ref _outstandingJobs);
        pool.Inject(job);
    }

    private bool WaitForCompletion(int millisecondsTimeout)
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

    // ── Test accessor ───────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor(JobScheduler scheduler)
    {
        public int OutstandingJobs => Volatile.Read(ref scheduler._outstandingJobs);

        /// <summary>
        /// Injects a job without waiting for completion. Allows tests to submit multiple
        /// independent jobs before waiting.
        /// </summary>
        public void Inject(Job job) => scheduler.InjectJob(job);

        public bool WaitForCompletion(int millisecondsTimeout = -1) =>
            scheduler.WaitForCompletion(millisecondsTimeout);
    }
}
