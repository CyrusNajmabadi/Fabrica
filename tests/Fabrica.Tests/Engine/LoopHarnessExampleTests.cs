using Fabrica.Engine;
using Fabrica.Engine.Simulation;
using Fabrica.Engine.World;
using Fabrica.Pipeline;
using Fabrica.Pipeline.Memory;
using Fabrica.Tests.Helpers;
using Xunit;

namespace Fabrica.Tests.Engine;

using ChainNode = BaseProductionLoop<WorldImage>.ChainNode;
using ChainNodeAllocator = BaseProductionLoop<WorldImage>.ChainNode.Allocator;

public sealed class LoopHarnessExampleTests
{
    [Fact]
    public void DeferredConsumerPinnedNode_SurvivesSimulationCleanup_UntilTaskCompletes()
    {
        var test = LoopHarness.Create();

        test.SimulationLoop.Bootstrap();
        test.ConsumptionLoop.RunIteration(); // pick up T0

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // T1
        var tick1Node = test.SimulationLoop.CurrentNode!;

        test.ConsumptionLoop.RunIteration(); // previous=T0, latest=T1; deferred dispatched for T1
        Assert.True(test.Pins.IsPinned(1));
        Assert.Equal(1, test.DeferredConsumer.InFlightCount);
        Assert.Equal(0, test.Shared.ConsumptionEpoch);

        for (var i = 0; i < 3; i++)
        {
            test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
            test.SimulationLoop.RunIteration();
            test.ConsumptionLoop.RunIteration();
        }

        Assert.True(test.Pins.IsPinned(1));
        Assert.False(tick1Node.IsUnreferenced);

        test.DeferredConsumer.CompletePendingTask(long.MaxValue);
        Assert.Equal(0, test.DeferredConsumer.InFlightCount);

        test.ConsumptionLoop.RunIteration();
        Assert.False(test.Pins.IsPinned(1));

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration();

        Assert.True(tick1Node.IsUnreferenced);
        Assert.Equal(0, test.SimulationLoop.PinnedQueueCount);
    }

    [Fact]
    public void DeferredConsumerPinAndExternalPin_BothMustClearBeforeSimulationCanReclaim()
    {
        var test = LoopHarness.Create();
        var externalOwner = new ExternalPinOwner();

        test.SimulationLoop.Bootstrap();
        test.ConsumptionLoop.RunIteration(); // pick up T0

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // T1
        var tick1Node = test.SimulationLoop.CurrentNode!;

        test.ConsumptionLoop.RunIteration(); // deferred dispatched for T1
        test.Pins.Pin(1, externalOwner);

        for (var i = 0; i < 3; i++)
        {
            test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
            test.SimulationLoop.RunIteration();
            test.ConsumptionLoop.RunIteration();
        }

        Assert.True(test.Pins.IsPinned(1));
        Assert.False(tick1Node.IsUnreferenced);

        test.DeferredConsumer.CompletePendingTask(long.MaxValue);
        test.ConsumptionLoop.RunIteration();

        Assert.True(test.Pins.IsPinned(1));
        Assert.False(tick1Node.IsUnreferenced);

        test.Pins.Unpin(1, externalOwner);

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration();

        Assert.False(test.Pins.IsPinned(1));
        Assert.True(tick1Node.IsUnreferenced);
        Assert.Equal(0, test.SimulationLoop.PinnedQueueCount);
    }

    [Fact]
    public void ConsumptionIterationBeforeSimulationIteration_SeesThePreviouslyPublishedSnapshot()
    {
        var test = LoopHarness.Create();

        test.SimulationLoop.Bootstrap();
        test.ConsumptionLoop.RunIteration(); // pick up T0, no consume yet

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // publish tick 1

        test.ConsumptionLoop.RunIteration(); // previous=T0, latest=T1, consume tick 1
        Assert.Equal([1], test.Renderer.RenderedTicks);
        Assert.Equal(0, test.Shared.ConsumptionEpoch);

        test.ConsumptionLoop.RunIteration(); // still consume tick 1
        Assert.Equal([1, 1], test.Renderer.RenderedTicks);
        Assert.Equal(0, test.Shared.ConsumptionEpoch);

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // publish tick 2

        test.ConsumptionLoop.RunIteration(); // previous=T1, latest=T2, consume tick 2
        Assert.Equal([1, 1, 2], test.Renderer.RenderedTicks);
        Assert.Equal(1, test.Shared.ConsumptionEpoch);
    }

    [Fact]
    public void UnpinnedSnapshot_IsReclaimedAfterConsumptionAdvancesPastIt()
    {
        var test = LoopHarness.CreateWithNoDeferredConsumers();

        test.SimulationLoop.Bootstrap();
        test.ConsumptionLoop.RunIteration(); // pick up T0

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // T1
        var tick1Node = test.SimulationLoop.CurrentNode!;

        test.ConsumptionLoop.RunIteration(); // consume T1, epoch=0

        for (var i = 0; i < 3; i++)
        {
            test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
            test.SimulationLoop.RunIteration();
            test.ConsumptionLoop.RunIteration();
        }

        Assert.True(tick1Node.IsUnreferenced);
        Assert.Equal(0, test.SimulationLoop.PinnedQueueCount);
    }

    [Fact]
    public void SimulationIterationBeforeConsumptionIteration_PublishesNewestSnapshotForConsumption()
    {
        var test = LoopHarness.CreateWithNoDeferredConsumers();

        test.SimulationLoop.Bootstrap();
        test.ConsumptionLoop.RunIteration(); // pick up T0

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 1
        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 2

        test.ConsumptionLoop.RunIteration(); // previous=T0, latest=T2

        Assert.Equal([2], test.Renderer.RenderedTicks);
        Assert.Equal(0, test.Shared.ConsumptionEpoch);
    }

    [Fact]
    public void DeferredConsumerDispatchFailure_AllowsLaterRetryOnTheSamePublishedSnapshot()
    {
        var test = LoopHarness.Create();

        test.SimulationLoop.Bootstrap();
        test.ConsumptionLoop.RunIteration(); // pick up T0

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 1

        test.DeferredConsumer.FailDispatchWith(new InvalidOperationException("dispatch failed"));

        var exception = Assert.Throws<InvalidOperationException>(
            test.ConsumptionLoop.RunIteration);

        Assert.Equal("dispatch failed", exception.Message);
        Assert.False(test.Pins.IsPinned(1));
        Assert.Equal(0, test.Shared.ConsumptionEpoch);
        Assert.Empty(test.Renderer.RenderedTicks);

        test.DeferredConsumer.ClearDispatchFailure();
        test.Clock.AdvanceBy(1_000_000_000L); // past ErrorRetryDelayNanoseconds so deferred consumer is due again
        test.ConsumptionLoop.RunIteration();

        Assert.Equal([1], test.Renderer.RenderedTicks);
        Assert.Equal(0, test.Shared.ConsumptionEpoch);
        Assert.True(test.Pins.IsPinned(1));
    }

    private sealed class LoopHarness
    {
        private readonly ProductionLoop<WorldImage, SimulationProducer, TestRecordingClock, TestNoOpWaiter>.TestAccessor _simulationAccessor;
        private long _simulationLastTime;
        private long _simulationAccumulator;

        private LoopHarness(
            SharedPipelineState<WorldImage> shared,
            TestClockState clockState,
            TestDeferredConsumerState deferredConsumerState,
            TestRendererState rendererState,
            ProductionLoop<WorldImage, SimulationProducer, TestRecordingClock, TestNoOpWaiter> productionLoop,
            ConsumptionLoop<WorldImage, TestRecordingConsumer, TestRecordingClock, TestNoOpWaiter> consumptionLoop)
        {
            this.Shared = shared;
            this.Clock = new ClockController(clockState);
            this.Pins = new PinController(shared.PinnedVersions);
            this.DeferredConsumer = new DeferredConsumerController(deferredConsumerState);
            this.ConsumptionLoop = new ConsumptionLoopController(consumptionLoop.GetTestAccessor());
            this.SimulationLoop = new SimulationLoopController(this, productionLoop.GetTestAccessor());
            this.Renderer = new RendererController(rendererState);

            _simulationAccessor = productionLoop.GetTestAccessor();
        }

        public SharedPipelineState<WorldImage> Shared { get; }

        public ClockController Clock { get; }

        public PinController Pins { get; }

        public DeferredConsumerController DeferredConsumer { get; }

        public RendererController Renderer { get; }

        public SimulationLoopController SimulationLoop { get; }

        public ConsumptionLoopController ConsumptionLoop { get; }

        public static LoopHarness Create(int poolSize = 8)
        {
            var deferredState = new TestDeferredConsumerState();
            return CreateInternal(poolSize, [new TestDeferredConsumer(deferredState)], deferredState);
        }

        public static LoopHarness CreateWithNoDeferredConsumers(int poolSize = 8) =>
            CreateInternal(poolSize, [], new TestDeferredConsumerState());

        private static LoopHarness CreateInternal(
            int poolSize,
            IDeferredConsumer<WorldImage>[] deferredConsumers,
            TestDeferredConsumerState deferredState)
        {
            var nodePool = new ObjectPool<ChainNode, ChainNodeAllocator>(poolSize);
            var imagePool = new ObjectPool<WorldImage, WorldImage.Allocator>(poolSize);
            var shared = new SharedPipelineState<WorldImage>();
            var clockState = new TestClockState();
            var rendererState = new TestRendererState();
            var clock = new TestRecordingClock(clockState);
            var waiter = new TestNoOpWaiter();
            var producer = new SimulationProducer(imagePool, 1);
            var consumer = new TestRecordingConsumer(rendererState);

            var productionLoop = new ProductionLoop<WorldImage, SimulationProducer, TestRecordingClock, TestNoOpWaiter>(
                nodePool, shared, producer, clock, waiter, TestPipelineConfiguration.Default);
            var consumptionLoop = new ConsumptionLoop<WorldImage, TestRecordingConsumer, TestRecordingClock, TestNoOpWaiter>(
                shared, consumer, clock, waiter, deferredConsumers, TestPipelineConfiguration.Default);

            return new LoopHarness(
                shared,
                clockState,
                deferredState,
                rendererState,
                productionLoop,
                consumptionLoop);
        }

        public sealed class SimulationLoopController(
            LoopHarness owner,
            ProductionLoop<WorldImage, SimulationProducer, TestRecordingClock, TestNoOpWaiter>.TestAccessor accessor)
        {
            private readonly LoopHarness _owner = owner;
            private readonly ProductionLoop<WorldImage, SimulationProducer, TestRecordingClock, TestNoOpWaiter>.TestAccessor _accessor = accessor;

            public ChainNode? CurrentNode => _accessor.CurrentNode;

            public int PinnedQueueCount => _accessor.PinnedQueueCount;

            public void Bootstrap()
            {
                _accessor.Bootstrap();
                _owner._simulationLastTime = _owner.Clock.NowNanoseconds;
                _owner._simulationAccumulator = 0;
            }

            public void RunIteration() => _accessor.RunOneIteration(
                    CancellationToken.None,
                    ref _owner._simulationLastTime,
                    ref _owner._simulationAccumulator);

        }

        public sealed class ConsumptionLoopController(
            ConsumptionLoop<WorldImage, TestRecordingConsumer, TestRecordingClock, TestNoOpWaiter>.TestAccessor accessor)
        {
            private readonly ConsumptionLoop<WorldImage, TestRecordingConsumer, TestRecordingClock, TestNoOpWaiter>.TestAccessor _accessor = accessor;

            public void RunIteration() => _accessor.RunOneIteration(CancellationToken.None);
        }

        public sealed class ClockController(TestClockState state)
        {
            private readonly TestClockState _state = state;

            public long NowNanoseconds => _state.NowNanoseconds;

            public void AdvanceBy(long nanoseconds) => _state.NowNanoseconds += nanoseconds;
        }

        public sealed class PinController(PinnedVersions pinnedVersions)
        {
            private readonly PinnedVersions _pinnedVersions = pinnedVersions;

            public void Pin(int sequenceNumber, IPinOwner owner) => _pinnedVersions.Pin(sequenceNumber, owner);

            public void Unpin(int sequenceNumber, IPinOwner owner) => _pinnedVersions.Unpin(sequenceNumber, owner);

            public bool IsPinned(int sequenceNumber) => _pinnedVersions.IsPinned(sequenceNumber);
        }

        public sealed class DeferredConsumerController(TestDeferredConsumerState state)
        {
            private readonly TestDeferredConsumerState _state = state;

            public int InFlightCount => _state.InFlightTasks.Count;

            public void FailDispatchWith(Exception exception) => _state.ExceptionToThrow = exception;

            public void ClearDispatchFailure() => _state.ExceptionToThrow = null;

            public void CompletePendingTask(long nextRunTime)
            {
                var taskCompletionSource = Assert.Single(_state.InFlightTasks);
                _state.InFlightTasks.Clear();
                taskCompletionSource.SetResult(nextRunTime);
            }
        }

        public sealed class RendererController(TestRendererState state)
        {
            private readonly TestRendererState _state = state;

            public IReadOnlyList<int> RenderedTicks => _state.RenderedTicks;

            public void FailWith(Exception exception) => _state.ExceptionToThrow = exception;

            public void ClearFailure() => _state.ExceptionToThrow = null;
        }
    }

    private sealed class TestDeferredConsumerState
    {
        public readonly List<TaskCompletionSource<long>> InFlightTasks = [];
        public Exception? ExceptionToThrow { get; set; }
    }

    private sealed class TestDeferredConsumer(TestDeferredConsumerState state) : IDeferredConsumer<WorldImage>
    {
        private readonly TestDeferredConsumerState _state = state;

        public long InitialDelayNanoseconds => 0L;

        public long ErrorRetryDelayNanoseconds => 1_000_000_000L;

        public Task<long> ConsumeAsync(WorldImage payload, CancellationToken cancellationToken)
        {
            if (_state.ExceptionToThrow is { } ex)
                throw ex;

            var taskCompletionSource = new TaskCompletionSource<long>();
            _state.InFlightTasks.Add(taskCompletionSource);
            return taskCompletionSource.Task;
        }
    }

    private sealed class TestRendererState
    {
        public readonly List<int> RenderedTicks = [];

        public Exception? ExceptionToThrow { get; set; }
    }

    private readonly struct TestRecordingConsumer(TestRendererState state) : IConsumer<WorldImage>
    {
        private readonly TestRendererState _state = state;

        public void Consume(ChainNode previous, ChainNode latest, long frameStartNanoseconds, CancellationToken cancellationToken)
        {
            if (_state.ExceptionToThrow is Exception exception)
                throw exception;
            _state.RenderedTicks.Add(latest.SequenceNumber);
        }
    }

    private sealed class ExternalPinOwner : IPinOwner;
}
