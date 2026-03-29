namespace Simulation.Engine;

/// <summary>
/// Monotonic clock abstraction.  All timestamps are in nanoseconds.
/// Concrete values are arbitrary; only differences are meaningful.
/// Injecting this interface allows tests to control time explicitly.
/// </summary>
internal interface IClock
{
    long NowNanoseconds { get; }
}
