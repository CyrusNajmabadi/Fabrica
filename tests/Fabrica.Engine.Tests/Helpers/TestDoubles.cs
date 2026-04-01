using Fabrica.Pipeline;

namespace Fabrica.Engine.Tests.Helpers;

/// <summary>Pipeline timing for tests — mirrors <see cref="SimulationConstants"/>.</summary>
internal static class TestPipelineConfiguration
{
    public static PipelineConfiguration Default => new()
    {
        TickDurationNanoseconds = SimulationConstants.TickDurationNanoseconds,
        IdleYieldNanoseconds = SimulationConstants.IdleYieldNanoseconds,
        PressureLowWaterMarkNanoseconds = SimulationConstants.PressureLowWaterMarkNanoseconds,
        PressureHardCeilingNanoseconds = SimulationConstants.PressureHardCeilingNanoseconds,
        PressureBucketCount = SimulationConstants.PressureBucketCount,
        PressureBaseDelayNanoseconds = SimulationConstants.PressureBaseDelayNanoseconds,
        PressureMaxDelayNanoseconds = SimulationConstants.PressureMaxDelayNanoseconds,
        RenderIntervalNanoseconds = SimulationConstants.RenderIntervalNanoseconds,
    };
}
