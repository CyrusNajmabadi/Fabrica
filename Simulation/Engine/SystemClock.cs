using System.Diagnostics;

namespace Simulation.Engine;

/// <summary>
/// Production clock backed by <see cref="Stopwatch"/>.
/// Converts hardware ticks to nanoseconds using pure integer arithmetic
/// to avoid floating-point error accumulation.
/// </summary>
internal sealed class SystemClock : IClock
{
    static SystemClock()
    {
        // Ensure remainder * 1_000_000_000L cannot overflow long.
        // remainder < Frequency, so we need Frequency < long.MaxValue / 1_000_000_000.
        // This covers hardware up to ~9.2 GHz tick rates.
        if (Stopwatch.Frequency >= long.MaxValue / 1_000_000_000L)
            throw new PlatformNotSupportedException(
                $"Stopwatch.Frequency ({Stopwatch.Frequency}) is too high for lossless nanosecond conversion.");
    }

    public long NowNanoseconds
    {
        get
        {
            long ticks     = Stopwatch.GetTimestamp();
            long seconds   = ticks / Stopwatch.Frequency;
            long remainder = ticks % Stopwatch.Frequency;
            // Safe: remainder < Frequency < long.MaxValue / 1_000_000_000 (checked above)
            return seconds * 1_000_000_000L + remainder * 1_000_000_000L / Stopwatch.Frequency;
        }
    }
}
