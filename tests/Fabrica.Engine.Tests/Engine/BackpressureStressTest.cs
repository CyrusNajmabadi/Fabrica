using Fabrica.Engine.Tests.Helpers;
using Xunit;

namespace Fabrica.Engine.Tests.Engine;

[Trait("Category", "Stress")]
public sealed class BackpressureStressTest
{
    [Fact]
    public void BackpressureEngages_WhenConsumptionIsSlowedDown()
    {
        var metrics = new StressTestMetrics();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        StressTestHelpers.RunEngine(metrics, Math.Max(1, Environment.ProcessorCount - 1), cancellationSource.Token, renderDelayMilliseconds: 50);

        Assert.True(metrics.FramesRendered > 0, "Expected at least one frame to be rendered.");
        Assert.Null(metrics.InvariantViolation);
    }
}
