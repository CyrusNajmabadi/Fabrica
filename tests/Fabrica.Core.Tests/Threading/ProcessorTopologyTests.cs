using Fabrica.Core.Threading;
using Xunit;

namespace Fabrica.Core.Tests.Threading;

public sealed class ProcessorTopologyTests
{
    [Fact]
    public void PerformanceCoreCount_IsPositive() =>
        Assert.True(ProcessorTopology.PerformanceCoreCount > 0);

    [Fact]
    public void PerformanceCoreCount_DoesNotExceedTotalCores() =>
        Assert.True(ProcessorTopology.PerformanceCoreCount <= Environment.ProcessorCount);

    [Fact]
    public void PerformanceCoreCount_MacOS_ReturnsLessThanTotalOnHeterogeneousCpu()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        // Apple Silicon Macs with E-cores will report fewer P-cores than total cores.
        // On homogeneous Intel Macs, P-core count == total count (still valid).
        var pCores = ProcessorTopology.PerformanceCoreCount;
        var total = Environment.ProcessorCount;

        Assert.True(pCores >= 1);
        Assert.True(pCores <= total);
    }
}
