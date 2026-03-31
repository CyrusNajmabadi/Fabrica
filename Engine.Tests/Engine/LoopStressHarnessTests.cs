using Engine;
using Engine.Memory;
using Engine.Pipeline;
using Engine.Simulation;
using Engine.Tests.Helpers;
using Engine.Threading;
using Engine.World;
using Xunit;

namespace Engine.Tests;

public sealed class LoopStressHarnessTests
{
    private static int LowWaterMarkTicks =>
        (int)(SimulationConstants.PressureLowWaterMarkNanoseconds / SimulationConstants.TickDurationNanoseconds);

    [Fact]
    public void SimulationIteration_AppliesBackpressure_WhenTickEpochGapExceedsLowWaterMark()
    {
        var test = LoopStressHarness.Create();

        test.SimulationLoop.Bootstrap();

        for (var i = 0; i < LowWaterMarkTicks + 1; i++)
        {
            test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
            test.SimulationLoop.RunIteration();
        }

        test.Waiter.ClearCalls();

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration();

        var expectedTick = LowWaterMarkTicks + 2;
        Assert.Equal(expectedTick, test.SimulationLoop.CurrentSequence);

        var idleYield = GetIdleYieldWait();
        Assert.True(test.Waiter.WaitCalls.Count >= 2, "Expected pressure delay + idle yield");
        Assert.True(test.Waiter.WaitCalls[0] > TimeSpan.Zero, "First wait should be a non-zero pressure delay");
        Assert.Equal(idleYield, test.Waiter.WaitCalls[^1]);
    }

    [Fact]
    public void SimulationIteration_NoPressure_WhenConsumptionKeepsUp()
    {
        var test = LoopStressHarness.Create();

        test.SimulationLoop.Bootstrap();

        for (var i = 0; i < 10; i++)
        {
            test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
            test.SimulationLoop.RunIteration();
            test.ConsumptionLoop.RunIteration();
        }

        test.Waiter.ClearCalls();

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration();

        var idleYield = GetIdleYieldWait();
        Assert.All(test.Waiter.WaitCalls, w => Assert.Equal(idleYield, w));
    }

    [Fact]
    public void SimulationIteration_PressureDecreases_WhenConsumptionAdvancesEpoch()
    {
        var test = LoopStressHarness.Create();

        test.SimulationLoop.Bootstrap();

        for (var i = 0; i < LowWaterMarkTicks + 3; i++)
        {
            test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
            test.SimulationLoop.RunIteration();
        }

        test.Waiter.ClearCalls();

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration();

        var pressureWaitsBeforeConsumption = test.Waiter.WaitCalls
            .Where(w => w > TimeSpan.Zero && w != GetIdleYieldWait())
            .ToList();
        Assert.NotEmpty(pressureWaitsBeforeConsumption);

        for (var i = 0; i < LowWaterMarkTicks + 3; i++)
            test.ConsumptionLoop.RunIteration();

        test.Waiter.ClearCalls();

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration();

        var idleYield = GetIdleYieldWait();
        Assert.All(test.Waiter.WaitCalls, w => Assert.Equal(idleYield, w));
    }

    private static TimeSpan GetIdleYieldWait() =>
        TimeSpan.FromTicks(SimulationConstants.IdleYieldNanoseconds / 100);

    private sealed class LoopStressHarness
    {
        private long _simulationLastTime;
        private long _simulationAccumulator;

        private LoopStressHarness(
            SharedState<WorldImage> shared,
            TestClockState clockState,
            TestWaiterState waiterState,
            ProductionLoop<WorldImage, SimulationProducer, TestRecordingClock, TestRecordingWaiter> simulationLoop,
            ConsumptionLoop<WorldImage, TestNoOpConsumer, TestRecordingClock, TestNoOpWaiter> consumptionLoop)
        {
            this.Shared = shared;
            this.Clock = new ClockController(clockState);
            this.Waiter = new WaiterController(waiterState);
            this.SimulationLoop = new SimulationLoopController(this, simulationLoop.GetTestAccessor());
            this.ConsumptionLoop = new ConsumptionLoopController(consumptionLoop.GetTestAccessor());
        }

        public SharedState<WorldImage> Shared { get; }

        public ClockController Clock { get; }

        public WaiterController Waiter { get; }

        public SimulationLoopController SimulationLoop { get; }

        public ConsumptionLoopController ConsumptionLoop { get; }

        public static LoopStressHarness Create(int poolSize = 64)
        {
            var nodePool = new ObjectPool<ChainNode<WorldImage>>(poolSize);
            var imagePool = new ObjectPool<WorldImage>(poolSize);
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

            return new LoopStressHarness(
                shared,
                clockState,
                waiterState,
                productionLoop,
                consumptionLoop);
        }

        public sealed class ClockController
        {
            private readonly TestClockState _state;

            public ClockController(TestClockState state) => _state = state;

            public void AdvanceBy(long nanoseconds) => _state.NowNanoseconds += nanoseconds;
        }

        public sealed class WaiterController
        {
            private readonly TestWaiterState _state;

            public WaiterController(TestWaiterState state) => _state = state;

            public IReadOnlyList<TimeSpan> WaitCalls => _state.WaitCalls;

            public Action<TimeSpan>? OnWait
            {
                get => _state.OnWait;
                set => _state.OnWait = value;
            }

            public void ClearCalls() => _state.WaitCalls.Clear();
        }

        public sealed class SimulationLoopController
        {
            private readonly LoopStressHarness _owner;
            private readonly ProductionLoop<WorldImage, SimulationProducer, TestRecordingClock, TestRecordingWaiter>.TestAccessor _accessor;

            public SimulationLoopController(
                LoopStressHarness owner,
                ProductionLoop<WorldImage, SimulationProducer, TestRecordingClock, TestRecordingWaiter>.TestAccessor accessor)
            {
                _owner = owner;
                _accessor = accessor;
            }

            public int CurrentSequence => _accessor.CurrentSequence;

            public ChainNode<WorldImage>? CurrentNode => _accessor.CurrentNode;

            public ChainNode<WorldImage>? OldestNode => _accessor.OldestNode;

            public void Bootstrap()
            {
                _accessor.Bootstrap();
                _owner._simulationLastTime = 0;
                _owner._simulationAccumulator = 0;
            }

            public void RunIteration() => _accessor.RunOneIteration(
                    CancellationToken.None,
                    ref _owner._simulationLastTime,
                    ref _owner._simulationAccumulator);
        }

        public sealed class ConsumptionLoopController
        {
            private readonly ConsumptionLoop<WorldImage, TestNoOpConsumer, TestRecordingClock, TestNoOpWaiter>.TestAccessor _accessor;

            public ConsumptionLoopController(
                ConsumptionLoop<WorldImage, TestNoOpConsumer, TestRecordingClock, TestNoOpWaiter>.TestAccessor accessor) => _accessor = accessor;

            public void RunIteration() => _accessor.RunOneIteration(CancellationToken.None);
        }
    }
}
