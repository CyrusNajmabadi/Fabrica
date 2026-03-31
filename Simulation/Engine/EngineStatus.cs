namespace Simulation.Engine;

/// <summary>
/// Non-simulation state from the engine: diagnostics, performance stats, etc.
/// Complementary to the simulation payload — the renderer receives this so it
/// can display system-level information (performance stats) alongside the game world.
/// </summary>
internal readonly struct EngineStatus
{
    public EngineStatistics Statistics { get; init; }
}

/// <summary>
/// Placeholder for future engine diagnostics: tick rate, pool pressure,
/// frame times, producer/consumer throughput, etc.
/// </summary>
internal readonly struct EngineStatistics
{
}
