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

        Assert.True(metrics.MaxTickObserved > 100,
            $"Expected many ticks with {workerCount} workers, but only observed {metrics.MaxTickObserved}.");
        Assert.Null(metrics.InvariantViolation);
    }
}
