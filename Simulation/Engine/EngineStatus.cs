namespace Simulation.Engine;

/// <summary>
/// Non-simulation state from the engine: save progress, diagnostics, etc.
/// Complementary to the simulation's <see cref="World.WorldSnapshot"/> — the
/// renderer receives both so it can display system-level information (save
/// indicators, performance stats) alongside the game world.
/// </summary>
internal readonly struct EngineStatus
{
    public SaveStatus Save { get; init; }
    public EngineStatistics Statistics { get; init; }
}

internal readonly struct SaveStatus
{
    public bool InFlight { get; init; }
    public SaveEvent? LastResult { get; init; }
}

/// <summary>
/// Records the outcome of a completed save operation.
/// Produced by the save task (threadpool) and delivered to the consumption
/// thread via a concurrent queue, so the renderer can show save feedback.
/// A null <see cref="Error"/> indicates success.
/// </summary>
internal readonly record struct SaveEvent(int Tick, long DurationNanoseconds, Exception? Error);

/// <summary>
/// Placeholder for future engine diagnostics: tick rate, pool pressure,
/// frame times, producer/consumer throughput, etc.
/// </summary>
internal readonly struct EngineStatistics
{
}
