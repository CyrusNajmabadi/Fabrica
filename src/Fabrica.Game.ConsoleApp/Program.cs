using Fabrica.Core.Collections;
using Fabrica.Core.Threading;
using Fabrica.Game;
using Fabrica.Pipeline;

using var cancellationSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    Console.WriteLine("Cancellation requested, shutting down…");
    cancellationSource.Cancel();
};

var config = new PipelineConfiguration
{
    TickDurationNanoseconds = 25_000_000,
    IdleYieldNanoseconds = 1_000_000,
    PressureLowWaterMarkNanoseconds = 50_000_000,
    PressureHardCeilingNanoseconds = 200_000_000,
    PressureBucketCount = 4,
    PressureBaseDelayNanoseconds = 1_000_000,
    PressureMaxDelayNanoseconds = 16_000_000,
    RenderIntervalNanoseconds = 16_666_667,
};

try
{
    var host = GameEngine.Create(
        new SystemClock(),
        new ThreadWaiter(),
        new NullGameConsumer(),
        config);

    await host.RunAsync(cancellationSource.Token).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Game shut down successfully.");
}

/// <summary>
/// No-op consumer: consumes pipeline entries without rendering. Placeholder until a real
/// game renderer is wired up.
/// </summary>
internal struct NullGameConsumer : IConsumer<GameWorldImage>
{
    public readonly void Consume(
        in ProducerConsumerQueue<PipelineEntry<GameWorldImage>>.Segment entries,
        long frameStartNanoseconds,
        CancellationToken cancellationToken)
    {
    }
}
