using Simulation.Engine;
using Xunit;

namespace Simulation.Tests.Engine;

public sealed class SimulationPressureTests
{
    private const long Tick = 25_000_000;  // one tick duration in ns (matches 40 Hz)
    private const long Lwm  = 100_000_000; // low water mark in ns (4 ticks)

    [Fact]
    public void ComputeDelay_ReturnsZero_WhenGapIsAtOrBelowLowWaterMark()
    {
        for (int ticks = -1; ticks <= 4; ticks++)
        {
            long gap = ticks * Tick;
            long delay = SimulationPressure.ComputeDelay(
                gapNanoseconds: gap,
                lowWaterMarkNanoseconds: Lwm,
                bucketWidthNanoseconds: Tick,
                bucketCount: 8,
                baseNanoseconds: 1_000_000,
                maxNanoseconds: 64_000_000);

            Assert.Equal(0, delay);
        }
    }

    [Fact]
    public void ComputeDelay_ScalesExponentially_AsGapGrowsBeyondLowWaterMark()
    {
        long[] expectedDelays =
        [
            1_000_000,  // 1 tick past LWM, bucket 0
            2_000_000,  // 2 ticks past LWM, bucket 1
            4_000_000,  // 3 ticks past LWM, bucket 2
            8_000_000,  // 4 ticks past LWM, bucket 3
            16_000_000, // 5 ticks past LWM, bucket 4
            32_000_000, // 6 ticks past LWM, bucket 5
            64_000_000, // 7 ticks past LWM, bucket 6
            64_000_000, // 8 ticks past LWM, bucket 7 (capped at max)
        ];

        for (int i = 0; i < expectedDelays.Length; i++)
        {
            long gap = Lwm + (i + 1) * Tick;
            long actual = SimulationPressure.ComputeDelay(
                gapNanoseconds: gap,
                lowWaterMarkNanoseconds: Lwm,
                bucketWidthNanoseconds: Tick,
                bucketCount: 8,
                baseNanoseconds: 1_000_000,
                maxNanoseconds: 64_000_000);

            Assert.Equal(expectedDelays[i], actual);
        }
    }

    [Fact]
    public void ComputeDelay_IsCappedAtMaximum()
    {
        long delay = SimulationPressure.ComputeDelay(
            gapNanoseconds: Lwm + 100 * Tick,
            lowWaterMarkNanoseconds: Lwm,
            bucketWidthNanoseconds: Tick,
            bucketCount: 8,
            baseNanoseconds: 1_000_000,
            maxNanoseconds: 64_000_000);

        Assert.Equal(64_000_000, delay);
    }

    [Fact]
    public void ComputeDelay_JustBarelyOverLowWaterMark_ReturnsBaseDelay()
    {
        long delay = SimulationPressure.ComputeDelay(
            gapNanoseconds: Lwm + 1,
            lowWaterMarkNanoseconds: Lwm,
            bucketWidthNanoseconds: Tick,
            bucketCount: 8,
            baseNanoseconds: 1_000_000,
            maxNanoseconds: 64_000_000);

        Assert.Equal(1_000_000, delay);
    }

    [Fact]
    public void ComputeDelay_WorksWithDifferentLowWaterMarks()
    {
        long customLwm = 250_000_000; // 250 ms

        Assert.Equal(0, SimulationPressure.ComputeDelay(
            gapNanoseconds: customLwm,
            lowWaterMarkNanoseconds: customLwm,
            bucketWidthNanoseconds: Tick,
            bucketCount: 8,
            baseNanoseconds: 1,
            maxNanoseconds: 64));

        Assert.Equal(1, SimulationPressure.ComputeDelay(
            gapNanoseconds: customLwm + 1,
            lowWaterMarkNanoseconds: customLwm,
            bucketWidthNanoseconds: Tick,
            bucketCount: 8,
            baseNanoseconds: 1,
            maxNanoseconds: 64));
    }
}
