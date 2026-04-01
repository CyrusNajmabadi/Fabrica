using Fabrica.Pipeline.Memory;
using Fabrica.Pipeline.Tests.Helpers;
using Xunit;

namespace Fabrica.Pipeline.Tests.Pipeline;

using ChainNode = BaseProductionLoop<TestPayload>.ChainNode;
using ChainNodeAllocator = BaseProductionLoop<TestPayload>.ChainNode.Allocator;

/// <summary>
/// Multi-phase behavioral tests that verify the backpressure feedback loop adapts dynamically to changing
/// production/consumption speed ratios.
///
/// Unlike the existing PipelineStressHarnessTests (which verify individual iterations), these tests run sustained phases of
/// many iterations and assert on aggregate behavior: total pressure delay, gap bounds, and transitions between pressured and
/// unpressured steady states.
///
/// All tests are deterministic — they use a controllable clock and recording waiter, stepping both loops single-threaded with
/// precise control over how many iterations each side runs per phase.
/// </summary>
public sealed class BackpressureAdaptationTests
{
    private static int LowWaterMarkTicks =>
        (int)(TestPipelineConfiguration.PressureLowWaterMarkNanoseconds / TestPipelineConfiguration.TickDurationNanoseconds);

    private static int HardCeilingTicks =>
        (int)(TestPipelineConfiguration.PressureHardCeilingNanoseconds / TestPipelineConfiguration.TickDurationNanoseconds);

    private static TimeSpan IdleYield =>
        TimeSpan.FromTicks(TestPipelineConfiguration.IdleYieldNanoseconds / 100);

    [Fact]
    public void MatchedRates_NoBackpressureOverSustainedRun()
    {
        var test = BackpressureHarness.Create();
        test.Bootstrap();

        for (var i = 0; i < 200; i++)
        {
            test.StepProduction();
            test.StepConsumption();
        }

        Assert.Equal(0, test.TotalPressureDelays);
        Assert.Equal(200, test.ProductionTick);
    }

    [Fact]
    public void SlowConsumption_CausesGrowingPressure()
    {
        var test = BackpressureHarness.Create();
        test.Bootstrap();

        var ticksToRun = LowWaterMarkTicks + 20;
        for (var i = 0; i < ticksToRun; i++)
            test.StepProduction();

        Assert.True(test.TotalPressureDelays > 0,
            "Expected backpressure delays when consumption is stalled.");
        Assert.True(test.ProductionTick > LowWaterMarkTicks,
            "Production should have advanced past the low water mark.");
    }

    [Fact]
    public void ConsumptionCatchesUp_PressureFullyReleases()
    {
        var test = BackpressureHarness.Create();
        test.Bootstrap();

        var phase1Ticks = LowWaterMarkTicks + 10;
        for (var i = 0; i < phase1Ticks; i++)
            test.StepProduction();

        Assert.True(test.TotalPressureDelays > 0, "Phase 1: expected pressure.");

        for (var i = 0; i < phase1Ticks + 5; i++)
        {
            test.StepProduction();
            test.StepConsumption();
        }

        test.ResetPressureCount();
        for (var i = 0; i < 20; i++)
        {
            test.StepProduction();
            test.StepConsumption();
        }

        Assert.Equal(0, test.TotalPressureDelays);
    }

    [Fact]
    public void PressureReengages_AfterConsumptionStallsAgain()
    {
        var test = BackpressureHarness.Create();
        test.Bootstrap();

        for (var i = 0; i < 50; i++)
        {
            test.StepProduction();
            test.StepConsumption();
        }
        Assert.Equal(0, test.TotalPressureDelays);

        for (var i = 0; i < LowWaterMarkTicks + 10; i++)
            test.StepProduction();
        Assert.True(test.TotalPressureDelays > 0, "Phase 2: expected pressure.");

        for (var i = 0; i < LowWaterMarkTicks + 15; i++)
        {
            test.StepProduction();
            test.StepConsumption();
        }
        test.ResetPressureCount();
        for (var i = 0; i < 20; i++)
        {
            test.StepProduction();
            test.StepConsumption();
        }
        Assert.Equal(0, test.TotalPressureDelays);

        test.ResetPressureCount();
        for (var i = 0; i < LowWaterMarkTicks + 10; i++)
            test.StepProduction();
        Assert.True(test.TotalPressureDelays > 0, "Phase 4: expected pressure to re-engage.");
    }

    [Fact]
    public void PressureDelaysGrowExponentially_AsGapWidens()
    {
        var test = BackpressureHarness.Create();
        test.Bootstrap();

        var delays = new List<TimeSpan>();
        for (var i = 0; i < LowWaterMarkTicks + 10; i++)
        {
            test.StepProduction();
            delays.AddRange(test.DrainPressureDelays());
        }

        Assert.True(delays.Count > 1,
            "Expected multiple pressure delays as the gap widened.");

        for (var i = 1; i < delays.Count; i++)
        {
            Assert.True(delays[i] >= delays[i - 1],
                $"Delay at index {i} ({delays[i]}) should be >= delay at index {i - 1} ({delays[i - 1]}).");
        }
    }

    [Fact]
    public void HardCeiling_BlocksProduction_UntilConsumptionAdvances()
    {
        var test = BackpressureHarness.Create();
        test.Bootstrap();

        for (var i = 0; i < HardCeilingTicks - 1; i++)
            test.StepProduction();

        var tickBeforeCeiling = test.ProductionTick;

        var waitCallsDuringCeiling = 0;
        test.OnWait = _ =>
        {
            waitCallsDuringCeiling++;
            if (waitCallsDuringCeiling == 1)
                test.AdvanceConsumptionEpochDirectly(tickBeforeCeiling);
        };

        test.StepProduction();
        test.OnWait = null;

        Assert.True(test.ProductionTick > tickBeforeCeiling,
            "Production should have advanced after consumption caught up.");
        Assert.True(waitCallsDuringCeiling >= 1,
            "Expected at least one hard-ceiling wait before consumption advanced.");
    }

    [Fact]
    public void GapRemainsWithinHardCeiling_UnderSustainedSlowConsumption()
    {
        var test = BackpressureHarness.Create();
        test.Bootstrap();

        for (var i = 0; i < 200; i++)
        {
            test.StepProduction();
            if (i % 4 == 0)
                test.StepConsumption();
        }

        var gapTicks = test.ProductionTick - test.ConsumptionEpoch;
        var gapNanoseconds = gapTicks * TestPipelineConfiguration.TickDurationNanoseconds;

        Assert.True(gapNanoseconds < TestPipelineConfiguration.PressureHardCeilingNanoseconds,
            $"Gap ({gapTicks} ticks = {gapNanoseconds / 1_000_000}ms) should stay below " +
            $"hard ceiling ({TestPipelineConfiguration.PressureHardCeilingNanoseconds / 1_000_000}ms).");
        Assert.True(test.TotalPressureDelays > 0,
            "Expected soft pressure delays to keep the gap bounded.");
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private sealed class BackpressureHarness
    {
        private readonly SharedPipelineState<TestPayload> _shared;
        private readonly TestClockState _clockState;
        private readonly TestWaiterState _waiterState;
        private readonly ProductionLoop<TestPayload, TestWorkerProducer, TestRecordingClock, TestRecordingWaiter>.TestAccessor _productionAccessor;
        private readonly ConsumptionLoop<TestPayload, TestNoOpConsumer, TestRecordingClock, TestNoOpWaiter>.TestAccessor _consumptionAccessor;
        private long _productionLastTime;
        private long _productionAccumulator;
        private int _totalPressureDelays;

        private BackpressureHarness(
            SharedPipelineState<TestPayload> shared,
            TestClockState clockState,
            TestWaiterState waiterState,
            ProductionLoop<TestPayload, TestWorkerProducer, TestRecordingClock, TestRecordingWaiter>.TestAccessor productionAccessor,
            ConsumptionLoop<TestPayload, TestNoOpConsumer, TestRecordingClock, TestNoOpWaiter>.TestAccessor consumptionAccessor)
        {
            _shared = shared;
            _clockState = clockState;
            _waiterState = waiterState;
            _productionAccessor = productionAccessor;
            _consumptionAccessor = consumptionAccessor;
        }

        public int ProductionTick => _productionAccessor.CurrentSequence;
        public int ConsumptionEpoch => _shared.ConsumptionEpoch;
        public int TotalPressureDelays => _totalPressureDelays;

        public Action<TimeSpan>? OnWait
        {
            get => _waiterState.OnWait;
            set => _waiterState.OnWait = value;
        }

        public static BackpressureHarness Create()
        {
            var nodePool = new ObjectPool<ChainNode, ChainNodeAllocator>(512);
            var payloadPool = new ObjectPool<TestPayload, TestPayload.Allocator>(512);
            var shared = new SharedPipelineState<TestPayload>();
            var clockState = new TestClockState();
            var waiterState = new TestWaiterState();
            var clock = new TestRecordingClock(clockState);
            var producer = new TestWorkerProducer(payloadPool, 1);

            var productionLoop = new ProductionLoop<TestPayload, TestWorkerProducer, TestRecordingClock, TestRecordingWaiter>(
                nodePool, shared, producer, clock, new TestRecordingWaiter(waiterState), TestPipelineConfiguration.Default);
            var consumptionLoop = new ConsumptionLoop<TestPayload, TestNoOpConsumer, TestRecordingClock, TestNoOpWaiter>(
                shared, new TestNoOpConsumer(), clock, new TestNoOpWaiter(), [], TestPipelineConfiguration.Default);

            return new BackpressureHarness(
                shared,
                clockState,
                waiterState,
                productionLoop.GetTestAccessor(),
                consumptionLoop.GetTestAccessor());
        }

        public void Bootstrap()
        {
            _productionAccessor.Bootstrap();
            _productionLastTime = 0;
            _productionAccumulator = 0;
        }

        public void StepProduction()
        {
            _clockState.NowNanoseconds += TestPipelineConfiguration.TickDurationNanoseconds;
            _waiterState.WaitCalls.Clear();

            _productionAccessor.RunOneIteration(
                CancellationToken.None,
                ref _productionLastTime,
                ref _productionAccumulator);

            _totalPressureDelays += CountPressureDelays(_waiterState.WaitCalls);
        }

        public void StepConsumption() =>
            _consumptionAccessor.RunOneIteration(CancellationToken.None);

        public void AdvanceConsumptionEpochDirectly(int epoch) =>
            _shared.ConsumptionEpoch = epoch;

        public void ResetPressureCount() => _totalPressureDelays = 0;

        public List<TimeSpan> DrainPressureDelays()
        {
            var delays = _waiterState.WaitCalls
                .Where(w => w > TimeSpan.Zero && w != IdleYield)
                .ToList();
            _waiterState.WaitCalls.Clear();
            return delays;
        }

        private static int CountPressureDelays(List<TimeSpan> waitCalls) =>
            waitCalls.Count(w => w > TimeSpan.Zero && w != IdleYield);
    }
}
