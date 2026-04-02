namespace Fabrica.Pipeline;

/// <summary>
/// A single entry in the pipeline's queue. Each tick produces one entry containing the simulation payload and the wall-clock
/// timestamp at which it was published. The entry's global position within the <see cref="Core.Collections.ProducerConsumerQueue{T}"/>
/// serves as its implicit sequence number — no explicit field is needed.
/// </summary>
public readonly struct PipelineEntry<TPayload>
{
    public required TPayload Payload { get; init; }

    /// <summary>
    /// Wall-clock timestamp (in nanoseconds) recorded when this entry was appended to the queue. Used exclusively by the
    /// consumption thread for rendering interpolation (computing how far past this tick real time has advanced). This value has
    /// no effect on simulation correctness — the simulation is purely deterministic and driven by the fixed-timestep accumulator,
    /// not wall-clock time.
    /// </summary>
    public required long PublishTimeNanoseconds { get; init; }
}
