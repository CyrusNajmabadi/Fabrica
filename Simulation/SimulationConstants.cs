namespace Simulation;

internal static class SimulationConstants
{
    public const int TicksPerSecond = 40;
    public const int TicksPerMinute = 2400;
    public const long TickDurationNanoseconds = 1_000_000_000L / TicksPerSecond; // 25_000_000

    // Belt units (future use)
    public const int UnitsPerItem = 240;
    public const int BeltLengthUnits = 11_520; // 48 items * 240

    // Belt speeds in units/tick (future use)
    public const int SpeedSlow = 3;   // 30 items/min
    public const int SpeedMedium = 30;  // 300 items/min
    public const int SpeedFast = 60;  // 600 items/min
    public const int SpeedMax = 120; // 1200 items/min

    // Memory pools
    public const int SnapshotPoolSize = 256;

    // Backpressure — delay inserted before each tick when simulation is ahead
    // of consumption by more than PressureLowWaterMarkNanoseconds.
    // Delay doubles for each additional tick-duration of gap beyond the low
    // water mark (binary exponential), capped at PressureMaxDelayNanoseconds.
    // If the gap reaches PressureHardCeilingNanoseconds the simulation blocks
    // entirely, sleeping PressureMaxDelayNanoseconds per iteration until the
    // gap drops below the ceiling.
    public const long PressureLowWaterMarkNanoseconds = 100_000_000L;   // 100 ms (~4 ticks at 40 Hz)
    public const long PressureHardCeilingNanoseconds = 2_000_000_000L; //   2 s  (~80 ticks at 40 Hz)
    public const int PressureBucketCount = 8;
    public const long PressureBaseDelayNanoseconds = 1_000_000L;     //   1 ms
    public const long PressureMaxDelayNanoseconds = 64_000_000L;    //  64 ms

    // Idle yield — brief sleep in the simulation loop when no ticks are due.
    public const long IdleYieldNanoseconds = 1_000_000L; // 1 ms

    // Save system
    public const int SaveIntervalTicks = TicksPerSecond * 60 * 5; // every 5 minutes

    // Consumption / rendering (~60 fps)
    public const long RenderIntervalNanoseconds = 1_000_000_000L / 60; // 16_666_666
}
