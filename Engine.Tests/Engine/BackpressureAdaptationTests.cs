using Engine;
using Engine.Memory;
using Engine.Pipeline;
using Engine.Simulation;
using Engine.Tests.Helpers;
using Engine.Threading;
using Engine.World;
using Xunit;

namespace Engine.Tests;

using ChainNode = BaseProductionLoop<WorldImage>.ChainNode;
using ChainNodeAllocator = BaseProductionLoop<WorldImage>.ChainNodeAllocator;

/// <summary>
/// Multi-phase behavioral tests that verify the backpressure feedback loop
/// adapts dynamically to changing simulation/consumption speed ratios.
///
/// Unlike the existing LoopStressHarnessTests (which verify individual
/// iterations), these tests run sustained phases of many iterations and
/// assert on aggregate behavior: total pressure delay, gap bounds, and
/// transitions between pressured and unpressured steady states.
///
/// All tests are deterministic — they use a controllable clock and recording
/// waiter, stepping both loops single-threaded with precise control over
/// how many iterations each side runs per phase.
/// </summary>
public sealed class BackpressureAdaptationTests
{
    private static int LowWaterMarkTicks =>
        (int)(SimulationConstants.PressureLowWaterMarkNanoseconds / SimulationConstants.TickDurationNanoseconds);

    private static int HardCeilingTicks =>
        (int)(SimulationConstants.PressureHardCeilingNanoseconds / SimulationConstants.TickDurationNanoseconds);

    private static TimeSpan IdleYield =>
        TimeSpan.FromTicks(SimulationConstants.IdleYieldNanoseconds / 100);

    [Fact]
    public void MatchedRates_NoBackpressureOverSustainedRun()
    {
        var test = BackpressureHarness.Create();
        test.Bootstrap();

        for (var i = 0; i < 200; i++)
        {
            test.StepSimulation();
            test.StepConsumption();
        }

        Assert.Equal(0, test.TotalPressureDelays);
        Assert.Equal(200, test.SimulationTick);
    }

    [Fact]
    public void SlowConsumption_CausesGrowingPressure()
    {
        var test = BackpressureHarness.Create();
        test.Bootstrap();

        var ticksToRun = LowWaterMarkTicks + 20;
        for (var i = 0; i < ticksToRun; i++)
            test.StepSimulation();

        Assert.True(test.TotalPressureDelays > 0,
            "Expected backpressure delays when consumption is stalled.");
        Assert.True(test.SimulationTick > LowWaterMarkTicks,
            "Simulation should have advanced past the low water mark.");
    }

    [Fact]
    public void ConsumptionCatchesUp_PressureFullyReleases()
    {
        var test = BackpressureHarness.Create();
        test.Bootstrap();

        var phase1Ticks = LowWaterMarkTicks + 10;
        for (var i = 0; i < phase1Ticks; i++)
            test.StepSimulation();

        Assert.True(test.TotalPressureDelays > 0, "Phase 1: expected pressure.");

        for (var i = 0; i < phase1Ticks + 5; i++)
        {
            test.StepSimulation();
            test.StepConsumption();
        }

        test.ResetPressureCount();
        for (var i = 0; i < 20; i++)
        {
            test.StepSimulation();
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
            test.StepSimulation();
            test.StepConsumption();
        }
        Assert.Equal(0, test.TotalPressureDelays);

        for (var i = 0; i < LowWaterMarkTicks + 10; i++)
            test.StepSimulation();
        Assert.True(test.TotalPressureDelays > 0, "Phase 2: expected pressure.");

        for (var i = 0; i < LowWaterMarkTicks + 15; i++)
        {
            test.StepSimulation();
            test.StepConsumption();
        }
        test.ResetPressureCount();
        for (var i = 0; i < 20; i++)
        {
            test.StepSimulation();
            test.StepConsumption();
        }
        Assert.Equal(0, test.TotalPressureDelays);

        test.ResetPressureCount();
        for (var i = 0; i < LowWaterMarkTicks + 10; i++)
            test.StepSimulation();
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
            test.StepSimulation();
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
    public void HardCeiling_BlocksSimulation_UntilConsumptionAdvances()
    {
        var test = BackpressureHarness.Create();
        test.Bootstrap();

        for (var i = 0; i < HardCeilingTicks - 1; i++)
            test.StepSimulation();

        var tickBeforeCeiling = test.SimulationTick;

        var waitCallsDuringCeiling = 0;
        test.OnWait = _ =>
        {
            waitCallsDuringCeiling++;
            if (waitCallsDuringCeiling == 1)
                test.AdvanceConsumptionEpochDirectly(tickBeforeCeiling);
        };

        test.StepSimulation();
        test.OnWait = null;

        Assert.True(test.SimulationTick > tickBeforeCeiling,
            "Simulation should have advanced after consumption caught up.");
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
            test.StepSimulation();
            if (i % 4 == 0)
                test.StepConsumption();
        }

        var gapTicks = test.SimulationTick - test.ConsumptionEpoch;
        var gapNanoseconds = gapTicks * SimulationConstants.TickDurationNanoseconds;

        Assert.True(gapNanoseconds < SimulationConstants.PressureHardCeilingNanoseconds,
            $"Gap ({gapTicks} ticks = {gapNanoseconds / 1_000_000}ms) should stay below " +
            $"hard ceiling ({SimulationConstants.PressureHardCeilingNanoseconds / 1_000_000}ms).");
        Assert.True(test.TotalPressureDelays > 0,
            "Expected soft pressure delays to keep the gap bounded.");
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private sealed class BackpressureHarness
    {
        private readonly SharedState<WorldImage> _shared;
        private readonly TestClockState _clockState;
        private readonly TestWaiterState _waiterState;
        private readonly ProductionLoop<WorldImage, SimulationProducer, TestRecordingClock, TestRecordingWaiter>.TestAccessor _simulationAccessor;
        private readonly ConsumptionLoop<WorldImage, TestNoOpConsumer, TestRecordingClock, TestNoOpWaiter>.TestAccessor _consumptionAccessor;
        private long _simulationLastTime;
        private long _simulationAccumulator;
        private int _totalPressureDelays;

        private BackpressureHarness(
            SharedState<WorldImage> shared,
            TestClockState clockState,
            TestWaiterState waiterState,
            ProductionLoop<WorldImage, SimulationProducer, TestRecordingClock, TestRecordingWaiter>.TestAccessor simulationAccessor,
            ConsumptionLoop<WorldImage, TestNoOpConsumer, TestRecordingClock, TestNoOpWaiter>.TestAccessor consumptionAccessor)
        {
            _shared = shared;
            _clockState = clockState;
            _waiterState = waiterState;
            _simulationAccessor = simulationAccessor;
            _consumptionAccessor = consumptionAccessor;
        }

        public int SimulationTick => _simulationAccessor.CurrentSequence;
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
            var imagePool = new ObjectPool<WorldImage, WorldImageAllocator>(512);
            var pinnedVersions = new PinnedVersions();
            var shared = new SharedState<WorldImage>();
            var clockState = new TestClockState();
            var waiterState = new TestWaiterState();
            var clock = new TestRecordingClock(clockState);
            var producer = new SimulationProducer(imagePool, new SimulationCoordinator(1));

            var productionLoop = new ProductionLoop<WorldImage, SimulationProducer, TestRecordingClock, TestRecordingWaiter>(
                nodePool, pinnedVersions, shared, producer, clock, new TestRecordingWaiter(waiterState));
            var consumptionLoop = new ConsumptionLoop<WorldImage, TestNoOpConsumer, TestRecordingClock, TestNoOpWaiter>(
                pinnedVersions, shared, new TestNoOpConsumer(), clock, new TestNoOpWaiter(), []);

            return new BackpressureHarness(
                shared,
                clockState,
                waiterState,
                productionLoop.GetTestAccessor(),
                consumptionLoop.GetTestAccessor());
        }

        public void Bootstrap()
        {
            _simulationAccessor.Bootstrap();
            _simulationLastTime = 0;
            _simulationAccumulator = 0;
        }

        public void StepSimulation()
        {
            _clockState.NowNanoseconds += SimulationConstants.TickDurationNanoseconds;
            _waiterState.WaitCalls.Clear();

            _simulationAccessor.RunOneIteration(
                CancellationToken.None,
                ref _simulationLastTime,
                ref _simulationAccumulator);

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
