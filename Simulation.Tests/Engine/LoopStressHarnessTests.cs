using Simulation.Engine;
using Simulation.Memory;
using Simulation.Tests.Helpers;
using Simulation.World;
using Xunit;

namespace Simulation.Tests.Engine;

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
        Assert.Equal(expectedTick, test.SimulationLoop.CurrentTick);

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
        test.Shared.NextSaveAtTick = 0;

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
        test.Shared.NextSaveAtTick = 0;

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
            SharedState shared,
            TestClockState clockState,
            TestWaiterState waiterState,
            SimulationLoop<TestRecordingClock, TestRecordingWaiter> simulationLoop,
            ConsumptionLoop<TestRecordingClock, TestNoOpWaiter, TestRecordingSaveRunner, TestRecordingSaver, TestNoOpRenderer> consumptionLoop)
        {
            this.Shared = shared;
            this.Clock = new ClockController(clockState);
            this.Waiter = new WaiterController(waiterState);
            this.SimulationLoop = new SimulationLoopController(this, simulationLoop.GetTestAccessor());
            this.ConsumptionLoop = new ConsumptionLoopController(consumptionLoop.GetTestAccessor());
        }

        public SharedState Shared { get; }

        public ClockController Clock { get; }

        public WaiterController Waiter { get; }

        public SimulationLoopController SimulationLoop { get; }

        public ConsumptionLoopController ConsumptionLoop { get; }

        public static LoopStressHarness Create(int poolSize = 64)
        {
            var memory = new MemorySystem(poolSize);
            var shared = new SharedState();
            var clockState = new TestClockState();
            var waiterState = new TestWaiterState();
            var clock = new TestRecordingClock(clockState);
            var simulationLoop = new SimulationLoop<TestRecordingClock, TestRecordingWaiter>(
                memory,
                shared,
                new Simulator(1),
                clock,
                new TestRecordingWaiter(waiterState));
            var consumptionLoop = new ConsumptionLoop<TestRecordingClock, TestNoOpWaiter, TestRecordingSaveRunner, TestRecordingSaver, TestNoOpRenderer>(
                memory,
                shared,
                clock,
                new TestNoOpWaiter(),
                new TestRecordingSaveRunner(),
                new TestRecordingSaver(),
                new TestNoOpRenderer(),
                new RenderCoordinator(1));

            return new LoopStressHarness(
                shared,
                clockState,
                waiterState,
                simulationLoop,
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
            private readonly SimulationLoop<TestRecordingClock, TestRecordingWaiter>.TestAccessor _accessor;

            public SimulationLoopController(
                LoopStressHarness owner,
                SimulationLoop<TestRecordingClock, TestRecordingWaiter>.TestAccessor accessor)
            {
                _owner = owner;
                _accessor = accessor;
            }

            public int CurrentTick => _accessor.CurrentTick;

            public WorldSnapshot? CurrentSnapshot => _accessor.CurrentSnapshot;

            public WorldSnapshot? OldestSnapshot => _accessor.OldestSnapshot;

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
            private readonly ConsumptionLoop<TestRecordingClock, TestNoOpWaiter, TestRecordingSaveRunner, TestRecordingSaver, TestNoOpRenderer>.TestAccessor _accessor;

            public ConsumptionLoopController(
                ConsumptionLoop<TestRecordingClock, TestNoOpWaiter, TestRecordingSaveRunner, TestRecordingSaver, TestNoOpRenderer>.TestAccessor accessor) => _accessor = accessor;

            public void RunIteration() => _accessor.RunOneIteration(CancellationToken.None);
        }
    }

    private readonly struct TestRecordingSaveRunner : ISaveRunner
    {
        public void RunSave(WorldImage image, int tick, Action<WorldImage, int> saveAction) =>
            throw new InvalidOperationException("Save dispatch is not part of this stress harness.");
    }

    private readonly struct TestRecordingSaver : ISaver
    {
        public void Save(WorldImage image, int tick) =>
            throw new InvalidOperationException("Save execution is not part of this stress harness.");
    }
}
