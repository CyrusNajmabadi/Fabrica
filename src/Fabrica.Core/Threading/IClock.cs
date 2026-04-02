namespace Fabrica.Core.Threading;

/// <summary>
/// Monotonic clock abstraction. All timestamps are in nanoseconds. Implementations are constrained to struct for zero
/// interface-dispatch overhead.
///
/// Concrete values are arbitrary — only differences (elapsed time) are meaningful. Injecting this abstraction lets tests control
/// time explicitly by advancing a counter, making timing-sensitive logic fully deterministic without real sleeps. See SystemClock
/// for the production implementation backed by Stopwatch.
/// </summary>
public interface IClock
{
    long NowNanoseconds { get; }
}
