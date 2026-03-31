using System.Diagnostics;

namespace Simulation.Engine;

/// <summary>
/// Production clock backed by <see cref="Stopwatch"/>.
/// Implemented as a <c>readonly struct</c> so that loops generic on
/// <typeparamref name="TClock"/> avoid interface dispatch entirely.
/// Converts hardware ticks to nanoseconds using pure integer arithmetic.
/// </summary>
internal readonly struct SystemClock : IClock
{
    static SystemClock()
    {
        // remainder * 1_000_000_000L must not overflow long.
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
            var ticks = Stopwatch.GetTimestamp();
            var seconds = ticks / Stopwatch.Frequency;
            var remainder = ticks % Stopwatch.Frequency;
            // Safe: remainder < Frequency < long.MaxValue / 1_000_000_000 (asserted above)
            return seconds * 1_000_000_000L + remainder * 1_000_000_000L / Stopwatch.Frequency;
        }
    }
}
