using Fabrica.Core.Memory;
using Fabrica.Pipeline.Tests.Helpers;
using Xunit;

namespace Fabrica.Pipeline.Tests.Pipeline;

using ChainNode = BaseProductionLoop<TestPayload>.ChainNode;
using ChainNodeAllocator = BaseProductionLoop<TestPayload>.ChainNode.Allocator;

public sealed class PipelineStressHarnessTests
{
    private static int LowWaterMarkTicks =>
        (int)(TestPipelineConfiguration.PressureLowWaterMarkNanoseconds / TestPipelineConfiguration.TickDurationNanoseconds);

    [Fact]
    public void ProductionIteration_AppliesBackpressure_WhenTickEpochGapExceedsLowWaterMark()
    {
        var test = LoopStressHarness.Create();

        test.ProductionLoop.Bootstrap();

        for (var i = 0; i < LowWaterMarkTicks + 1; i++)
        {
            test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
            test.ProductionLoop.RunIteration();
        }

        test.Waiter.ClearCalls();

        test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
        test.ProductionLoop.RunIteration();

        var expectedTick = LowWaterMarkTicks + 2;
        Assert.Equal(expectedTick, test.ProductionLoop.CurrentSequence);

        var idleYield = GetIdleYieldWait();
        Assert.True(test.Waiter.WaitCalls.Count >= 2, "Expected pressure delay + idle yield");
        Assert.True(test.Waiter.WaitCalls[0] > TimeSpan.Zero, "First wait should be a non-zero pressure delay");
        Assert.Equal(idleYield, test.Waiter.WaitCalls[^1]);
    }

    [Fact]
    public void ProductionIteration_NoPressure_WhenConsumptionKeepsUp()
    {
        var test = LoopStressHarness.Create();

        test.ProductionLoop.Bootstrap();
        test.ConsumptionLoop.RunIteration(); // pick up T0

        for (var i = 0; i < 10; i++)
        {
            test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
            test.ProductionLoop.RunIteration();
            test.ConsumptionLoop.RunIteration();
        }

        test.Waiter.ClearCalls();

        test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
        test.ProductionLoop.RunIteration();

        var idleYield = GetIdleYieldWait();
        Assert.All(test.Waiter.WaitCalls, w => Assert.Equal(idleYield, w));
    }

    [Fact]
    public void ProductionIteration_PressureDecreases_WhenConsumptionAdvancesEpoch()
    {
        var test = LoopStressHarness.Create();

        test.ProductionLoop.Bootstrap();
        test.ConsumptionLoop.RunIteration(); // pick up T0

        for (var i = 0; i < LowWaterMarkTicks + 3; i++)
        {
            test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
            test.ProductionLoop.RunIteration();
        }

        test.Waiter.ClearCalls();

        test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
        test.ProductionLoop.RunIteration();

        var pressureWaitsBeforeConsumption = test.Waiter.WaitCalls
            .Where(w => w > TimeSpan.Zero && w != GetIdleYieldWait())
            .ToList();
        Assert.NotEmpty(pressureWaitsBeforeConsumption);

        // Advance epoch by producing new ticks and letting consumption catch up. Each consumption iteration advances the
        // epoch to _previous.SequenceNumber, and _previous only changes when a new LatestNode appears.
        for (var i = 0; i < LowWaterMarkTicks + 3; i++)
        {
            test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
            test.ProductionLoop.RunIteration();
            test.ConsumptionLoop.RunIteration();
        }

        test.Waiter.ClearCalls();

        test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
        test.ProductionLoop.RunIteration();

        var idleYield = GetIdleYieldWait();
        Assert.All(test.Waiter.WaitCalls, w => Assert.Equal(idleYield, w));
    }

    private static TimeSpan GetIdleYieldWait() =>
        TimeSpan.FromTicks(TestPipelineConfiguration.IdleYieldNanoseconds / 100);

    private sealed class LoopStressHarness
    {
        private long _productionLastTime;
        private long _productionAccumulator;

        private LoopStressHarness(
            SharedPipelineState<TestPayload> shared,
            TestClockState clockState,
            TestWaiterState waiterState,
            ProductionLoop<TestPayload, TestWorkerProducer, TestRecordingClock, TestRecordingWaiter> productionLoop,
            ConsumptionLoop<TestPayload, TestNoOpConsumer, TestRecordingClock, TestNoOpWaiter> consumptionLoop)
        {
            this.Shared = shared;
            this.Clock = new ClockController(clockState);
            this.Waiter = new WaiterController(waiterState);
            this.ProductionLoop = new ProductionLoopController(this, productionLoop.GetTestAccessor());
            this.ConsumptionLoop = new ConsumptionLoopController(consumptionLoop.GetTestAccessor());
        }

        public SharedPipelineState<TestPayload> Shared { get; }

        public ClockController Clock { get; }

        public WaiterController Waiter { get; }

        public ProductionLoopController ProductionLoop { get; }

        public ConsumptionLoopController ConsumptionLoop { get; }

        public static LoopStressHarness Create(int poolSize = 64)
        {
            var nodePool = new ObjectPool<ChainNode, ChainNodeAllocator>(poolSize);
            var payloadPool = new ObjectPool<TestPayload, TestPayload.Allocator>(poolSize);
            var shared = new SharedPipelineState<TestPayload>();
            var clockState = new TestClockState();
            var waiterState = new TestWaiterState();
            var clock = new TestRecordingClock(clockState);
            var producer = new TestWorkerProducer(payloadPool, 1);

            var productionLoop = new ProductionLoop<TestPayload, TestWorkerProducer, TestRecordingClock, TestRecordingWaiter>(
                nodePool, shared, producer, clock, new TestRecordingWaiter(waiterState), TestPipelineConfiguration.Default);
            var consumptionLoop = new ConsumptionLoop<TestPayload, TestNoOpConsumer, TestRecordingClock, TestNoOpWaiter>(
                shared, new TestNoOpConsumer(), clock, new TestNoOpWaiter(), [], TestPipelineConfiguration.Default);

            return new LoopStressHarness(
                shared,
                clockState,
                waiterState,
                productionLoop,
                consumptionLoop);
        }

        public sealed class ClockController(TestClockState state)
        {
            private readonly TestClockState _state = state;

            public void AdvanceBy(long nanoseconds) => _state.NowNanoseconds += nanoseconds;
        }

        public sealed class WaiterController(TestWaiterState state)
        {
            private readonly TestWaiterState _state = state;

            public IReadOnlyList<TimeSpan> WaitCalls => _state.WaitCalls;

            public Action<TimeSpan>? OnWait
            {
                get => _state.OnWait;
                set => _state.OnWait = value;
            }

            public void ClearCalls() => _state.WaitCalls.Clear();
        }

        public sealed class ProductionLoopController(
            LoopStressHarness owner,
            ProductionLoop<TestPayload, TestWorkerProducer, TestRecordingClock, TestRecordingWaiter>.TestAccessor accessor)
        {
            private readonly LoopStressHarness _owner = owner;
            private readonly ProductionLoop<TestPayload, TestWorkerProducer, TestRecordingClock, TestRecordingWaiter>.TestAccessor _accessor = accessor;

            public int CurrentSequence => _accessor.CurrentSequence;

            public ChainNode? CurrentNode => _accessor.CurrentNode;

            public ChainNode? OldestNode => _accessor.OldestNode;

            public void Bootstrap()
            {
                _accessor.Bootstrap();
                _owner._productionLastTime = 0;
                _owner._productionAccumulator = 0;
            }

            public void RunIteration() => _accessor.RunOneIteration(
                    CancellationToken.None,
                    ref _owner._productionLastTime,
                    ref _owner._productionAccumulator);
        }

        public sealed class ConsumptionLoopController(
            ConsumptionLoop<TestPayload, TestNoOpConsumer, TestRecordingClock, TestNoOpWaiter>.TestAccessor accessor)
        {
            private readonly ConsumptionLoop<TestPayload, TestNoOpConsumer, TestRecordingClock, TestNoOpWaiter>.TestAccessor _accessor = accessor;

            public void RunIteration() => _accessor.RunOneIteration(CancellationToken.None);
        }
    }
}
