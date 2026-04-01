namespace Fabrica.Pipeline;

/// <summary>
/// Configuration values for the production and consumption loops. Passed at construction time so the pipeline layer has no
/// dependency on domain-specific constants. The engine layer constructs this from <c>SimulationConstants</c>.
/// </summary>
public readonly record struct PipelineConfiguration
{
    // ── Production ────────────────────────────────────────────────────────────

    /// <summary>Duration of one simulation tick in nanoseconds.</summary>
    public required long TickDurationNanoseconds { get; init; }

    /// <summary>Brief sleep in the production loop when no ticks are due.</summary>
    public required long IdleYieldNanoseconds { get; init; }

    // ── Backpressure ──────────────────────────────────────────────────────────

    /// <summary>Epoch gap (in nanoseconds) below which no delay is applied.</summary>
    public required long PressureLowWaterMarkNanoseconds { get; init; }

    /// <summary>Epoch gap at which the production loop blocks entirely.</summary>
    public required long PressureHardCeilingNanoseconds { get; init; }

    /// <summary>Number of exponential delay buckets between low water mark and hard ceiling.</summary>
    public required int PressureBucketCount { get; init; }

    /// <summary>Base delay (1 ms) for the first backpressure bucket.</summary>
    public required long PressureBaseDelayNanoseconds { get; init; }

    /// <summary>Maximum per-tick delay and the sleep duration when at the hard ceiling.</summary>
    public required long PressureMaxDelayNanoseconds { get; init; }

    // ── Consumption ───────────────────────────────────────────────────────────

    /// <summary>Target frame interval for the consumption loop (e.g. ~16.67 ms for 60 fps).</summary>
    public required long RenderIntervalNanoseconds { get; init; }
}
