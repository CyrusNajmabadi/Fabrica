using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fabrica.Core.Threading;
using Fabrica.Core.Threading.Queues;

namespace Fabrica.Core.Jobs;

/// <summary>
/// Per-worker internal context owned by <see cref="WorkerPool"/>. Each worker thread has its own
/// instance, providing access to the thread's local queue, steal offset, and current scheduler.
/// Not exposed to job subclasses — they receive a <see cref="JobContext"/> instead.
///
/// THREAD MODEL
///   <see cref="Enqueue"/> calls <see cref="BoundedLocalQueue{T}.Push"/>, which is an owner-only
///   operation. This is safe because <see cref="Enqueue"/> is only called during
///   <see cref="Job.Execute"/>, which runs on the thread that owns this context's queue.
/// </summary>
internal sealed class WorkerContext(WorkerPool pool, int workerIndex, StrongBox<InjectionQueue<Job>> overflow)
{
    internal readonly int WorkerIndex = workerIndex;
    internal BoundedLocalQueue<Job> Deque = new(overflow);

    /// <summary>
    /// Per-worker PRNG for randomizing steal target selection. Seeded uniquely per worker via the
    /// golden-ratio hash to ensure spatial decorrelation — avoids pathological lock-step where all
    /// workers try to steal from the same victim simultaneously.
    /// </summary>
    internal FastRand StealRand = new((ulong)workerIndex * 0x9E3779B97F4A7C15);

    /// <summary>
    /// Whether this worker is currently in a searching state (HotSpin or WarmYield). Accessed only
    /// by the owning thread — no synchronization needed. Used by <see cref="WorkerPool"/> to track
    /// whether the worker's transition out of searching should cascade-wake a parked worker.
    /// </summary>
    internal bool IsSearching;

    // ── Instrumentation ──────────────────────────────────────────────────

    /// <summary>How the most recent job was obtained. Only set when INSTRUMENT is defined.</summary>
    internal JobSource LastJobSource;

    /// <summary>Pre-allocated per-worker buffer. Non-null when instrumentation is active.</summary>
    internal SchedulerRecord[]? InstrumentRecords;

    /// <summary>Number of records written so far in the current instrumentation session.</summary>
    internal int InstrumentRecordCount;

    /// <summary>
    /// Pushes a ready-to-execute sub-job onto this worker's deque. The job's scheduler (set at
    /// creation time) is used to increment the outstanding count. No scheduler write occurs —
    /// the job is already bound to its scheduler.
    /// </summary>
    internal void Enqueue(Job job)
    {
#if DEBUG
        Debug.Assert(job.State == JobState.Pending);
        job.State = JobState.Queued;
#endif

        // Tell the scheduler about the new in-flight job *before* making it visible to workers.
        // This ordering is essential: if we pushed first and a worker executed+completed the job
        // before we incremented, the scheduler could see outstanding == 0 prematurely and signal
        // completion while work is still running.
        job.Scheduler.IncrementOutstanding();

        // Push onto this worker's deque (hot slot, then ring buffer). The owning thread is most
        // likely to pop it back (cache-hot), but idle workers can steal from the ring (FIFO end).
        Deque.Push(job);

        // Wake any parked workers so they can steal the newly available work.
        pool.NotifyWorkAvailable();
    }
}
