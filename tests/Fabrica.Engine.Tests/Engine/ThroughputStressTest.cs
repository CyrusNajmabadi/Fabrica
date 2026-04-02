using Fabrica.Engine.Tests.Helpers;
using Xunit;

namespace Fabrica.Engine.Tests.Engine;

[Trait("Category", "Stress")]
public sealed class ThroughputStressTest
{
    [Fact]
    public void SustainsHighThroughput_NoDeadlocks()
    {
        var metrics = new StressTestMetrics();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        StressTestHelpers.RunEngine(metrics, Math.Max(1, Environment.ProcessorCount - 1), cancellationSource.Token, renderDelayMilliseconds: 0);

        Assert.True(metrics.FramesRendered > 0, "Expected at least one frame to be rendered.");
        Assert.Null(metrics.InvariantViolation);
    }
}
