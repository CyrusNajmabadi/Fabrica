using Simulation.Engine;
using Xunit;

namespace Simulation.Tests.Engine;

public sealed class SimulationPressureTests
{
    [Fact]
    public void ComputeDelay_ReturnsZero_AtOrAboveThreshold()
    {
        Assert.Equal(0, SimulationPressure.ComputeDelay(
            available: 8,
            capacity: 16,
            baseNanoseconds: 5,
            maxNanoseconds: 50));

        Assert.Equal(0, SimulationPressure.ComputeDelay(
            available: 16,
            capacity: 16,
            baseNanoseconds: 5,
            maxNanoseconds: 50));
    }

    [Fact]
    public void ComputeDelay_DoublesEachTimeAvailabilityHalves()
    {
        const long baseDelay = 5;
        const long maxDelay = 100;

        Assert.Equal(10, SimulationPressure.ComputeDelay(
            available: 4,
            capacity: 16,
            baseNanoseconds: baseDelay,
            maxNanoseconds: maxDelay));

        Assert.Equal(20, SimulationPressure.ComputeDelay(
            available: 2,
            capacity: 16,
            baseNanoseconds: baseDelay,
            maxNanoseconds: maxDelay));

        Assert.Equal(40, SimulationPressure.ComputeDelay(
            available: 1,
            capacity: 16,
            baseNanoseconds: baseDelay,
            maxNanoseconds: maxDelay));
    }

    [Fact]
    public void ComputeDelay_IsCappedAtMaximum()
    {
        Assert.Equal(50, SimulationPressure.ComputeDelay(
            available: 1,
            capacity: 1024,
            baseNanoseconds: 5,
            maxNanoseconds: 50));

        Assert.Equal(50, SimulationPressure.ComputeDelay(
            available: 0,
            capacity: 1024,
            baseNanoseconds: 5,
            maxNanoseconds: 50));
    }
}
