using Engine.Memory;

namespace Engine.Pipeline;

/// <summary>
/// A slow consumer that runs asynchronously on a configurable schedule.
///
/// Extends <see cref="IPinOwner"/> so the consumption loop can pass the
/// consumer itself as the pin identity to <see cref="PinnedVersions"/> —
/// no synthetic owner objects needed.
///
/// The consumption loop auto-pins the node before calling <see cref="ConsumeAsync"/>
/// and auto-unpins it when the returned task completes.  The consumer receives
/// only the payload (not the chain node) because it has no need for chain mechanics.
///
/// The returned <see cref="Task{Long}"/> contains the next wall-clock nanosecond
/// timestamp at which this consumer should run again.  The loop inserts this into
/// a min-heap so the hot-path check is O(1) — no virtual calls or iteration over
/// consumers when nothing is due.
///
/// SCHEDULING
///   Each deferred consumer is registered with an initial delay.  The consumption
///   loop converts this to an absolute timestamp at startup and inserts it into
///   the priority queue.  When the task completes, the returned value replaces
///   the entry in the queue.
///
/// EXAMPLE: saving is one deferred consumer.  Its ConsumeAsync dispatches the
/// actual I/O to a threadpool task and returns the next save time when done.
/// </summary>
internal interface IDeferredConsumer<in TPayload> : IPinOwner
{
    public Task<long> ConsumeAsync(TPayload payload, int sequenceNumber, CancellationToken cancellationToken);
}

/// <summary>
/// Pairs a deferred consumer with the delay (in nanoseconds) from the loop's
/// start time until the consumer's first eligible run.
/// </summary>
internal readonly record struct DeferredConsumerRegistration<TPayload>(
    IDeferredConsumer<TPayload> Consumer,
    long InitialDelayNanoseconds);
