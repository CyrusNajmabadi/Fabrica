namespace Simulation;

internal static class SimulationConstants
{
    public const int  TicksPerSecond  = 40;
    public const int  TicksPerMinute  = 2400;
    public const long TickDurationNanoseconds = 1_000_000_000L / TicksPerSecond; // 25_000_000

    // Belt units (future use)
    public const int UnitsPerItem    = 240;
    public const int BeltLengthUnits = 11_520; // 48 items * 240

    // Belt speeds in units/tick (future use)
    public const int SpeedSlow   = 3;   // 30 items/min
    public const int SpeedMedium = 30;  // 300 items/min
    public const int SpeedFast   = 60;  // 600 items/min
    public const int SpeedMax    = 120; // 1200 items/min

    // Memory pools
    public const int SnapshotPoolSize = 256;

    // Backpressure — delay inserted before each tick when pool is under pressure.
    // Delay doubles for each additional halving of available slots (binary exponential).
    // Applied when available slots fall below SnapshotPoolSize / 2.
    public const int PressureBucketCount = 8;
    public const long PressureBaseDelayNanoseconds = 1_000_000L;  //  1 ms
    public const long PressureMaxDelayNanoseconds = 64_000_000L; // 64 ms
    public const long PoolEmptyRetryNanoseconds = 1_000_000L;  //  1 ms

    // Save system
    public const int SaveIntervalTicks = TicksPerSecond * 60 * 5; // every 5 minutes

    // Consumption / rendering (~60 fps)
    public const long RenderIntervalNanoseconds = 1_000_000_000L / 60; // 16_666_666
}
