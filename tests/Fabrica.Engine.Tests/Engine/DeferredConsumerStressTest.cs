using Fabrica.Engine.Tests.Helpers;
using Xunit;

namespace Fabrica.Engine.Tests.Engine;

[Trait("Category", "Stress")]
public sealed class DeferredConsumerStressTest
{
    [Fact]
    public void DeferredConsumerPinning_AcrossThreadBoundaries()
    {
        var metrics = new StressTestMetrics();
        var saveMetrics = new SaveMetrics();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        StressTestHelpers.RunEngineWithDeferredSave(metrics, saveMetrics, Math.Max(1, Environment.ProcessorCount - 1), cancellationSource.Token);

        Assert.True(metrics.FramesRendered > 0, "Expected at least one frame to be rendered.");
        Assert.True(saveMetrics.SavesCompleted > 0,
            "Expected at least one save to complete.");
        Assert.Equal(0, saveMetrics.SavesFailed);
        Assert.Null(metrics.InvariantViolation);
    }
}
