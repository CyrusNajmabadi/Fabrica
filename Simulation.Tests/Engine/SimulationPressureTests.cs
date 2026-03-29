using Simulation.Engine;
using Xunit;

namespace Simulation.Tests.Engine;

public sealed class SimulationPressureTests
{
    [Fact]
    public void ComputeDelay_UsesExpectedBuckets_ForEveryAvailabilityValue()
    {
        long[] expectedDelays =
        [
            0,  // 16 available
            0,  // 15 available
            0,  // 14 available
            1,  // 13 available
            1,  // 12 available
            2,  // 11 available
            2,  // 10 available
            4,  // 9 available
            4,  // 8 available
            8,  // 7 available
            8,  // 6 available
            16, // 5 available
            16, // 4 available
            32, // 3 available
            32, // 2 available
            64, // 1 available
            64, // 0 available
        ];

        for (int available = 16; available >= 0; available--)
        {
            long expected = expectedDelays[16 - available];
            long actual = SimulationPressure.ComputeDelay(
                available: available,
                capacity: 16,
                baseNanoseconds: 1,
                maxNanoseconds: 64);

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ComputeDelay_IsCappedAtMaximum()
    {
        Assert.Equal(64, SimulationPressure.ComputeDelay(
            available: 1,
            capacity: 1024,
            baseNanoseconds: 1,
            maxNanoseconds: 64));

        Assert.Equal(64, SimulationPressure.ComputeDelay(
            available: 0,
            capacity: 1024,
            baseNanoseconds: 1,
            maxNanoseconds: 64));
    }

    [Fact]
    public void ComputeDelay_ReturnsMaximum_WhenCapacityIsNonPositive()
    {
        Assert.Equal(64, SimulationPressure.ComputeDelay(
            available: 0,
            capacity: 0,
            baseNanoseconds: 1,
            maxNanoseconds: 64));
    }
}
