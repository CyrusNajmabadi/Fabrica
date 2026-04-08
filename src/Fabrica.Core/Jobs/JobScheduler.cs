using System.Diagnostics;

namespace Fabrica.Core.Jobs;

/// <summary>
/// Lightweight per-DAG completion tracker. Multiple schedulers share a single
/// <see cref="WorkerPool"/>, allowing independent DAGs (e.g. simulation and rendering) to execute
/// concurrently on the same workers.
///
/// <para>
/// Each scheduler owns a coordinator slot in the pool. When <see cref="Submit"/> is called, the
/// root job is pushed directly onto the coordinator's own deque and the calling thread participates
/// as a worker until the DAG drains. This eliminates the cross-thread wake latency that would
/// otherwise dominate small DAGs.
/// </para>
///
/// USAGE
///   1. Create a <see cref="WorkerPool"/> with <c>coordinatorCount &gt;= 1</c>.
///   2. Create a <see cref="JobScheduler"/> per logical pipeline (sim, render). Each claims one
///      coordinator slot.
///   3. Call <see cref="Submit"/> with a root job. The coordinator thread participates in execution
///      until the entire DAG drains.
///   4. Repeat from step 3 for the next batch.
///
/// ONE DAG AT A TIME
///   <see cref="Submit"/> runs the coordinator work loop until the DAG completes, so only one DAG
///   is in flight per scheduler at a time.
/// </summary>
public sealed class JobScheduler(WorkerPool pool)
{
    private readonly WorkerContext _coordinatorContext = pool.AttachCoordinator();

    /// <summary>
    /// Number of jobs that have been enqueued but not yet completed. Incremented on every enqueue
    /// (submit, sub-job enqueue, dependency propagation); decremented after each execution
    /// completes. Reaches zero exactly when all DAG work is done.
    /// </summary>
    private int _outstandingJobs;

    // ── Coordinator API ─────────────────────────────────────────────────────

    /// <summary>
    /// Submits a root job and participates as a worker until the entire DAG (including sub-jobs
    /// and dependents) completes. The calling thread runs the coordinator work loop, executing
    /// jobs from its own deque, stealing from peers, and draining the injection queue.
    ///
    /// Thread pinning and QoS are NOT handled here — the caller is responsible for creating the
    /// calling thread with <see cref="Threading.ThreadPinningNative.StartNativeThreadWithHighQos"/>
    /// (see <c>Host.StartLoopTask</c> and <c>WorkerPool</c> constructor).
    /// </summary>
    public void Submit(Job job)
    {
        Debug.Assert(Volatile.Read(ref _outstandingJobs) == 0, "Submit called while a DAG is already in flight.");

#if DEBUG
        Debug.Assert(job.State == JobState.Pending);
        job.State = JobState.Queued;
#endif
        job.Scheduler = this;
        Interlocked.Increment(ref _outstandingJobs);
        _coordinatorContext.Deque.Push(job);
        pool.NotifyWorkAvailable();

        this.RunUntilComplete();
    }

    // ── Called by WorkerPool execution infrastructure ────────────────────────

    internal void IncrementOutstanding() => Interlocked.Increment(ref _outstandingJobs);

    internal void DecrementOutstanding() => Interlocked.Decrement(ref _outstandingJobs);

    // ── Private ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Injects a job via the shared injection queue (used by the test accessor for multi-inject
    /// patterns). The coordinator work loop will pick it up along with background workers.
    /// </summary>
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

    /// <summary>
    /// Spins the coordinator thread as a worker until all outstanding jobs complete. The
    /// coordinator participates fully in work-stealing: it pops from its own deque, steals from
    /// peers, and drains the injection queue — just like a background worker.
    /// </summary>
    private void RunUntilComplete()
    {
        var spinWait = new SpinWait();
        while (Volatile.Read(ref _outstandingJobs) > 0)
        {
            if (pool.TryExecuteOne(_coordinatorContext))
            {
                spinWait.Reset();
                continue;
            }

            spinWait.SpinOnce();
        }
    }

    // ── Test accessor ───────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor(JobScheduler scheduler)
    {
        public int OutstandingJobs => Volatile.Read(ref scheduler._outstandingJobs);

        /// <summary>
        /// Submits a root job and runs the coordinator work loop until the entire DAG completes.
        /// </summary>
        public void Submit(Job job)
        {
            scheduler.InjectJob(job);
            scheduler.RunUntilComplete();
        }

        /// <summary>
        /// Injects a job via the shared injection queue without waiting for completion. Allows
        /// tests to submit multiple independent jobs before calling <see cref="WaitForCompletion"/>.
        /// </summary>
        public void Inject(Job job) => scheduler.InjectJob(job);

        /// <summary>
        /// Runs the coordinator work loop until all outstanding jobs complete.
        /// </summary>
        public void WaitForCompletion() => scheduler.RunUntilComplete();
    }
}
