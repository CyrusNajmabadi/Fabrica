namespace Fabrica.Pipeline;

/// <summary>
/// A single entry in the pipeline's queue. Each tick produces one entry containing the simulation payload, the simulation tick
/// that produced it, and the wall-clock timestamp at which it was published.
/// </summary>
public readonly struct PipelineEntry<TPayload>
{
    public required TPayload Payload { get; init; }

    /// <summary>
    /// Zero-based simulation tick that produced this entry. Bootstrap is tick 0; subsequent ticks increment monotonically.
    /// Useful for diagnostics, test assertions, and (in the future) determining how much state is shared between consecutive
    /// world snapshots.
    /// </summary>
    public required long Tick { get; init; }

    /// <summary>
    /// Wall-clock timestamp (in nanoseconds) recorded when this entry was appended to the queue. Used exclusively by the
    /// consumption thread for rendering interpolation (computing how far past this tick real time has advanced). This value has
    /// no effect on simulation correctness — the simulation is purely deterministic and driven by the fixed-timestep accumulator,
    /// not wall-clock time.
    /// </summary>
    public required long PublishTimeNanoseconds { get; init; }
}
