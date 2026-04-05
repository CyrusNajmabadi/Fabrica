namespace Fabrica.Core.Jobs;

/// <summary>
/// Atomic dependency counter for fork-join job scheduling. Initialized with the number of
/// dependencies a job must wait for; each completed dependency calls <see cref="Decrement"/>.
/// When the count reaches zero, the job is ready to run.
///
/// USAGE PATTERN
///   A parent job creates a child job with <c>Counter = new JobCounter(N)</c> where N is the
///   number of prerequisite jobs. Each prerequisite holds a reference to the child's counter.
///   When a prerequisite completes, it calls <see cref="Decrement"/>. Exactly one thread
///   observes the zero transition (the return value is <c>true</c>) and is responsible for
///   enqueuing the now-ready child job.
///
/// MEMORY MODEL
///   <see cref="Decrement"/> uses <see cref="Interlocked.Decrement(ref int)"/> which provides
///   a full memory barrier. This guarantees that all writes made by a completing prerequisite
///   are visible to the thread that observes the zero transition — and therefore to the
///   dependent job when it executes.
///
/// PERFORMANCE
///   One <c>Interlocked.Decrement</c> (~10-20ns) per dependency edge. With chunky jobs
///   (hundreds to thousands of operations each), this overhead is negligible.
///
/// PORTABILITY
///   Maps directly to <c>std::atomic&lt;int&gt;</c> (C++) or <c>AtomicI32</c> (Rust).
/// </summary>
internal struct JobCounter(int count)
{
    private int _remaining = count;

    /// <summary>True when all dependencies have completed (remaining count is zero).</summary>
    public readonly bool IsComplete => Volatile.Read(in _remaining) == 0;

    /// <summary>
    /// Atomically decrements the remaining count. Returns <c>true</c> if this call brought the
    /// count to zero — meaning the dependent job is now ready. Exactly one thread will observe
    /// <c>true</c>; that thread is responsible for enqueuing the dependent job.
    /// </summary>
    public bool Decrement()
        => Interlocked.Decrement(ref _remaining) == 0;
}
