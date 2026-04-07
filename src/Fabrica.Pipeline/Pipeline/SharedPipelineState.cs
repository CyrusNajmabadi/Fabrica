using Fabrica.Core.Threading.Queues;

namespace Fabrica.Pipeline;

/// <summary>
/// All shared objects accessible to both the production and consumption threads. Acts as a convenient grouping — the actual
/// cross-thread synchronization lives inside the <see cref="ProducerConsumerQueue{T}"/> (volatile producer/consumer positions)
/// and <see cref="PinnedVersions"/> (concurrent dictionary).
///
/// CROSS-THREAD COMMUNICATION
///   The <see cref="Queue"/> is the single point of SPSC communication between the two threads. The producer appends entries and
///   the consumer acquires/advances through them. Both operations use volatile fences internally — no additional synchronization
///   is needed.
///
/// PinnedVersions
///   Thread-safe registry of queue positions that deferred consumers are still using. The consumption thread pins before
///   dispatching; threadpool tasks unpin on completion; the production thread reads IsPinned during cleanup. See
///   <see cref="PinnedVersions"/> for full concurrency details.
/// </summary>
public sealed class SharedPipelineState<TPayload>(ProducerConsumerQueue<PipelineEntry<TPayload>> queue)
{
    /// <summary>
    /// Thread-safe. Written by consumption thread and threadpool tasks. Read by production thread during cleanup.
    /// </summary>
    public readonly PinnedVersions PinnedVersions = new();

    /// <summary>
    /// Lock-free SPSC queue carrying pipeline entries from the production thread to the consumption thread. The queue owns the
    /// volatile producer/consumer positions that replace the old LatestNode and ConsumptionEpoch fields.
    /// </summary>
    public ProducerConsumerQueue<PipelineEntry<TPayload>> Queue { get; } = queue;
}
