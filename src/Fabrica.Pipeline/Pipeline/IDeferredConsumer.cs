namespace Fabrica.Pipeline;

/// <summary>
/// A slow consumer that runs asynchronously on a configurable schedule.
///
/// Extends <see cref="IPinOwner"/> so the consumption loop can pass the consumer itself as the pin identity to
/// <see cref="PinnedVersions"/> — no synthetic owner objects needed. The consumption loop auto-pins the node before calling
/// <see cref="ConsumeAsync"/> and auto-unpins it when the returned task completes. See <see cref="PinnedVersions"/> for the full
/// pinning protocol.
///
/// The returned <see cref="Task{Long}"/> contains the next wall-clock nanosecond timestamp at which this consumer should run
/// again.
///
/// SCHEDULING
///   <see cref="InitialDelayNanoseconds"/> controls how long after the loop starts
///   before the first run.  The consumption loop converts this to an absolute
///   timestamp at startup and inserts it into the priority queue.  When the task
///   completes, the returned value replaces the entry in the queue.  On failure,
///   the consumer is re-scheduled after <see cref="ErrorRetryDelayNanoseconds"/>.
///
/// EXAMPLE: saving is one deferred consumer. Its ConsumeAsync dispatches the actual I/O to a threadpool task and returns the next
/// save time when done.
/// </summary>
public interface IDeferredConsumer<in TPayload> : IPinOwner
{
    /// <summary>Delay (in nanoseconds) from loop start until the first eligible run.</summary>
    long InitialDelayNanoseconds { get; }

    /// <summary>
    /// Delay (in nanoseconds) before retrying after a failed <see cref="ConsumeAsync"/> task.
    /// </summary>
    long ErrorRetryDelayNanoseconds { get; }

    Task<long> ConsumeAsync(TPayload payload, CancellationToken cancellationToken);
}
