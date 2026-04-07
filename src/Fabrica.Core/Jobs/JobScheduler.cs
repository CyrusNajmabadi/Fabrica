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
    /// Unparks the coordinator in <see cref="Submit"/> when outstanding work reaches zero.
    /// </summary>
    private readonly ManualResetEventSlim _completionSignal = new(false);

    // ── Coordinator API ─────────────────────────────────────────────────────

    /// <summary>
    /// Submits a root job and blocks until the entire DAG (including sub-jobs and dependents)
    /// completes.
    /// </summary>
    public void Submit(Job job)
    {
        this.InjectJob(job);
        this.WaitForCompletion();
    }

    // ── Called by WorkerPool execution infrastructure ────────────────────────

    // Called by WorkerPool when a sub-job is enqueued or a dependent becomes ready.
    internal void IncrementOutstanding() => Interlocked.Increment(ref _outstandingJobs);

    // Atomically decrements outstanding count; signals _completionSignal when it reaches zero. Called by WorkerPool
    // after a job finishes execution.
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

    private void WaitForCompletion()
    {
        if (Volatile.Read(ref _outstandingJobs) == 0)
            return;

        // Reset at the start of each wait cycle.
        _completionSignal.Reset();

        if (Volatile.Read(ref _outstandingJobs) == 0)
            return;

        _completionSignal.Wait();
    }

    // ── Test accessor ───────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor(JobScheduler scheduler)
    {
        public int OutstandingJobs => Volatile.Read(ref scheduler._outstandingJobs);

        /// <summary>
        /// Submits a root job and blocks until the entire DAG completes.
        /// </summary>
        public void Submit(Job job)
        {
            scheduler.InjectJob(job);
            scheduler.WaitForCompletion();
        }

        /// <summary>
        /// Injects a job without waiting for completion. Allows tests to submit multiple
        /// independent jobs before waiting.
        /// </summary>
        public void Inject(Job job) => scheduler.InjectJob(job);

        public void WaitForCompletion() =>
            scheduler.WaitForCompletion();
    }
}
