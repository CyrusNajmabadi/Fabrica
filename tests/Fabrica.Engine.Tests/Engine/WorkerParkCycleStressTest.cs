using Fabrica.Engine.Tests.Helpers;
using Xunit;

namespace Fabrica.Engine.Tests.Engine;

[Trait("Category", "Stress")]
public sealed class WorkerParkCycleStressTest
{
    [Fact]
    public void WorkerSignalParkCycle_SurvivesManyTicks()
    {
        var workerCount = Math.Max(4, Environment.ProcessorCount);
        var metrics = new StressTestMetrics();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        StressTestHelpers.RunEngine(metrics, workerCount, cancellationSource.Token, renderDelayMilliseconds: 0);

        Assert.True(metrics.FramesRendered > 50,
            $"Expected many frames with {workerCount} workers, but only observed {metrics.FramesRendered}.");
        Assert.Null(metrics.InvariantViolation);
    }
}
