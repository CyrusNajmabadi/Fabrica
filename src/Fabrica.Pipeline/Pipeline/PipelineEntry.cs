namespace Fabrica.Pipeline;

/// <summary>
/// A single entry in the pipeline's queue. Each tick produces one entry containing the simulation payload and the wall-clock
/// timestamp at which it was published. The entry's global position within the <see cref="Core.Collections.ProducerConsumerQueue{T}"/>
/// serves as its implicit sequence number — no explicit field is needed.
/// </summary>
public readonly struct PipelineEntry<TPayload>
{
    public required TPayload Payload { get; init; }
    public required long PublishTimeNanoseconds { get; init; }
}
