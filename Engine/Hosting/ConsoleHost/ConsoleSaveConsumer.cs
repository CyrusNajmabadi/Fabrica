using System.Diagnostics;
using Engine.Pipeline;
using Engine.World;

namespace Engine.Hosting.ConsoleHost;

/// <summary>
/// Deferred consumer that performs periodic saves on the thread pool.
///
/// Replaces the old ISaver/ISaveRunner/TaskSaveRunner pipeline with a single <see cref="IDeferredConsumer{TPayload}"/>
/// implementation. The consumption loop handles pinning/unpinning and scheduling automatically.
/// </summary>
internal sealed class ConsoleSaveConsumer(long intervalNanoseconds) : IDeferredConsumer<WorldImage>
{
    public long InitialDelayNanoseconds => intervalNanoseconds;

    public long ErrorRetryDelayNanoseconds => intervalNanoseconds;

    // Capturing lambda — allocated once per save cycle (seconds-scale interval), not on a hot path.
    public Task<long> ConsumeAsync(WorldImage payload, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            Console.WriteLine("[Save]   saving...");
            Thread.Sleep(1000);
            Console.WriteLine("[Save]   done.");
            return NowNanoseconds() + intervalNanoseconds;
        }, cancellationToken);

    private static long NowNanoseconds()
    {
        var ticks = Stopwatch.GetTimestamp();
        var seconds = ticks / Stopwatch.Frequency;
        var remainder = ticks % Stopwatch.Frequency;
        return (seconds * 1_000_000_000L) + (remainder * 1_000_000_000L / Stopwatch.Frequency);
    }
}
