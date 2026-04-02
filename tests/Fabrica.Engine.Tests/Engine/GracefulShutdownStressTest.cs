using System.Diagnostics;
using Fabrica.Engine.Tests.Helpers;
using Xunit;

namespace Fabrica.Engine.Tests.Engine;

[Trait("Category", "Stress")]
public sealed class GracefulShutdownStressTest
{
    [Fact]
    public void GracefulShutdown_UnderLoad()
    {
        var metrics = new StressTestMetrics();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var stopwatch = Stopwatch.StartNew();
        StressTestHelpers.RunEngine(metrics, Math.Max(1, Environment.ProcessorCount - 1), cancellationSource.Token, renderDelayMilliseconds: 0);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Engine did not shut down within 10 seconds (took {stopwatch.Elapsed}). Possible deadlock.");
        Assert.Null(metrics.InvariantViolation);
    }
}
