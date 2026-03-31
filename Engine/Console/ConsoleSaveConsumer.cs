using System.Diagnostics;
using Engine.Pipeline;
using Engine.World;

namespace Engine.Console;

/// <summary>
/// Deferred consumer that performs periodic saves on the thread pool.
///
/// Replaces the old ISaver/ISaveRunner/TaskSaveRunner pipeline with a single
/// <see cref="IDeferredConsumer{TPayload}"/> implementation.  The consumption
/// loop handles pinning/unpinning and scheduling automatically.
/// </summary>
internal sealed class ConsoleSaveConsumer : IDeferredConsumer<WorldImage>
{
    private readonly long _intervalNanoseconds;

    public ConsoleSaveConsumer(long intervalNanoseconds) =>
        _intervalNanoseconds = intervalNanoseconds;

    public Task<long> ConsumeAsync(WorldImage payload, int sequenceNumber, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            global::System.Console.WriteLine($"[Save]   tick={sequenceNumber} — saving...");
            Thread.Sleep(1000);
            global::System.Console.WriteLine($"[Save]   tick={sequenceNumber} — done.");
            return NowNanoseconds() + _intervalNanoseconds;
        }, cancellationToken);

    private static long NowNanoseconds()
    {
        var ticks = Stopwatch.GetTimestamp();
        var seconds = ticks / Stopwatch.Frequency;
        var remainder = ticks % Stopwatch.Frequency;
        return seconds * 1_000_000_000L + remainder * 1_000_000_000L / Stopwatch.Frequency;
    }
}
