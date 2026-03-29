namespace Simulation;

internal static class SimConstants
{
    public const int TicksPerSecond  = 40;
    public const int TicksPerMinute  = 2400;
    public const double TickDurationMs = 1000.0 / TicksPerSecond; // 25 ms

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

    // Backpressure — logarithmic delay added before each tick
    public const double PressureHighWater  = 0.5;  // start slowing when pool is 50% utilized
    public const double PressureBaseDelayMs = 5.0;
    public const double PressureMaxDelayMs  = 50.0;
    public const int    PoolEmptyRetryMs    = 1;   // poll interval when pool is fully exhausted

    // Save system
    public const int SaveIntervalTicks = TicksPerSecond * 60 * 5; // 5 minutes

    // Consumption / rendering
    public const int RenderIntervalMs = 16; // ~60 fps
}
