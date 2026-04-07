using Fabrica.Core.Memory;
using Fabrica.Core.Threading.Queues;
using Fabrica.Pipeline.Tests.Helpers;
using Xunit;

namespace Fabrica.Pipeline.Tests.Pipeline;

public sealed class PipelineHarnessTests
{
    [Fact]
    public void DeferredConsumerPinnedNode_SurvivesCleanup_UntilTaskCompletes()
    {
        var test = LoopHarness.Create();

        test.ProductionLoop.Bootstrap();
        test.ConsumptionLoop.RunIteration(); // pick up T0 (pos 0), count=1, skip

        test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
        test.ProductionLoop.RunIteration(); // publish pos 1

        test.ConsumptionLoop.RunIteration(); // [0,1], deferred for latest (pos 1)
        Assert.True(test.Pins.IsPinned(1));
        Assert.Equal(1, test.DeferredConsumer.InFlightCount);
        Assert.Equal(1, test.Shared.Queue.ConsumerPosition);

        for (var i = 0; i < 3; i++)
        {
            test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
            test.ProductionLoop.RunIteration();
            test.ConsumptionLoop.RunIteration();
        }

        Assert.True(test.Pins.IsPinned(1));
        Assert.NotEqual(0, test.ProductionLoop.PinnedPayloadCount);

        test.DeferredConsumer.CompletePendingTask(long.MaxValue);
        Assert.Equal(0, test.DeferredConsumer.InFlightCount);

        test.ConsumptionLoop.RunIteration();
        Assert.False(test.Pins.IsPinned(1));

        test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
        test.ProductionLoop.RunIteration();

        Assert.Equal(0, test.ProductionLoop.PinnedPayloadCount);
    }

    [Fact]
    public void DeferredConsumerPinAndExternalPin_BothMustClearBeforeReclaim()
    {
        var test = LoopHarness.Create();
        var externalOwner = new ExternalPinOwner();

        test.ProductionLoop.Bootstrap();
        test.ConsumptionLoop.RunIteration(); // pick up T0

        test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
        test.ProductionLoop.RunIteration(); // pos 1

        test.ConsumptionLoop.RunIteration(); // deferred dispatched for pos 1
        test.Pins.Pin(1, externalOwner);

        for (var i = 0; i < 3; i++)
        {
            test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
            test.ProductionLoop.RunIteration();
            test.ConsumptionLoop.RunIteration();
        }

        Assert.True(test.Pins.IsPinned(1));
        Assert.NotEqual(0, test.ProductionLoop.PinnedPayloadCount);

        test.DeferredConsumer.CompletePendingTask(long.MaxValue);
        test.ConsumptionLoop.RunIteration();

        Assert.True(test.Pins.IsPinned(1));
        Assert.NotEqual(0, test.ProductionLoop.PinnedPayloadCount);

        test.Pins.Unpin(1, externalOwner);

        test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
        test.ProductionLoop.RunIteration();

        Assert.False(test.Pins.IsPinned(1));
        Assert.Equal(0, test.ProductionLoop.PinnedPayloadCount);
    }

    [Fact]
    public void ConsumptionIterationBeforeProductionIteration_SeesThePreviouslyPublishedSnapshot()
    {
        var test = LoopHarness.Create();

        test.ProductionLoop.Bootstrap();
        test.ConsumptionLoop.RunIteration(); // pick up T0

        test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
        test.ProductionLoop.RunIteration(); // publish pos 1

        test.ConsumptionLoop.RunIteration(); // [0,1], consume, latest position 1
        Assert.Equal([1L], test.Renderer.RenderedPositions);
        Assert.Equal(1, test.Shared.Queue.ConsumerPosition);

        test.ConsumptionLoop.RunIteration(); // [1], count=1, skip — no re-render
        Assert.Equal([1L], test.Renderer.RenderedPositions);
        Assert.Equal(1, test.Shared.Queue.ConsumerPosition);

        test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
        test.ProductionLoop.RunIteration(); // publish pos 2

        test.ConsumptionLoop.RunIteration(); // [1,2], latest position 2
        Assert.Equal([1L, 2L], test.Renderer.RenderedPositions);
        Assert.Equal(2, test.Shared.Queue.ConsumerPosition);
    }

    [Fact]
    public void UnpinnedSnapshot_IsReclaimedAfterConsumptionAdvancesPastIt()
    {
        var test = LoopHarness.CreateWithNoDeferredConsumers();

        test.ProductionLoop.Bootstrap();
        test.ConsumptionLoop.RunIteration(); // pick up T0

        test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
        test.ProductionLoop.RunIteration(); // pos 1

        test.ConsumptionLoop.RunIteration(); // consume through pos 1

        for (var i = 0; i < 3; i++)
        {
            test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
            test.ProductionLoop.RunIteration();
            test.ConsumptionLoop.RunIteration();
        }

        Assert.Equal(0, test.ProductionLoop.PinnedPayloadCount);
    }

    [Fact]
    public void ProductionIterationBeforeConsumptionIteration_PublishesNewestSnapshotForConsumption()
    {
        var test = LoopHarness.CreateWithNoDeferredConsumers();

        test.ProductionLoop.Bootstrap();
        test.ConsumptionLoop.RunIteration(); // pick up T0

        test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
        test.ProductionLoop.RunIteration(); // pos 1
        test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
        test.ProductionLoop.RunIteration(); // pos 2

        test.ConsumptionLoop.RunIteration(); // [0,1,2], latest position 2

        Assert.Equal([2L], test.Renderer.RenderedPositions);
        Assert.Equal(2, test.Shared.Queue.ConsumerPosition);
    }

    [Fact]
    public void DeferredConsumerDispatchFailure_AllowsLaterRetryOnTheSamePublishedSnapshot()
    {
        var test = LoopHarness.Create();

        test.ProductionLoop.Bootstrap();
        test.ConsumptionLoop.RunIteration(); // pick up T0

        test.Clock.AdvanceBy(TestPipelineConfiguration.TickDurationNanoseconds);
        test.ProductionLoop.RunIteration(); // pos 1

        test.DeferredConsumer.FailDispatchWith(new InvalidOperationException("dispatch failed"));

        var exception = Assert.Throws<InvalidOperationException>(
            test.ConsumptionLoop.RunIteration);

        Assert.Equal("dispatch failed", exception.Message);
        Assert.False(test.Pins.IsPinned(1));
        Assert.Equal(0, test.Shared.Queue.ConsumerPosition);
        Assert.Empty(test.Renderer.RenderedPositions);

        test.DeferredConsumer.ClearDispatchFailure();
        test.Clock.AdvanceBy(1_000_000_000L); // past ErrorRetryDelayNanoseconds so deferred consumer is due again
        test.ConsumptionLoop.RunIteration();

        Assert.Equal([1L], test.Renderer.RenderedPositions);
        Assert.Equal(1, test.Shared.Queue.ConsumerPosition);
        Assert.True(test.Pins.IsPinned(1));
    }

    private sealed class LoopHarness
    {
        private long _productionLastTime;
        private long _productionAccumulator;

        private LoopHarness(
            SharedPipelineState<TestPayload> shared,
            TestClockState clockState,
            TestDeferredConsumerState deferredConsumerState,
            TestRendererState rendererState,
            ProductionLoop<TestPayload, TestWorkerProducer, TestRecordingClock, TestNoOpWaiter> productionLoop,
            ConsumptionLoop<TestPayload, TestRecordingConsumer, TestRecordingClock, TestNoOpWaiter> consumptionLoop)
        {
            this.Shared = shared;
            this.Clock = new ClockController(clockState);
            this.Pins = new PinController(shared.PinnedVersions);
            this.DeferredConsumer = new DeferredConsumerController(deferredConsumerState);
            this.ConsumptionLoop = new ConsumptionLoopController(consumptionLoop.GetTestAccessor());
            this.ProductionLoop = new ProductionLoopController(this, productionLoop.GetTestAccessor());
            this.Renderer = new RendererController(rendererState);
        }

        public SharedPipelineState<TestPayload> Shared { get; }

        public ClockController Clock { get; }

        public PinController Pins { get; }

        public DeferredConsumerController DeferredConsumer { get; }

        public RendererController Renderer { get; }

        public ProductionLoopController ProductionLoop { get; }

        public ConsumptionLoopController ConsumptionLoop { get; }

        public static LoopHarness Create(int poolSize = 8)
        {
            var deferredState = new TestDeferredConsumerState();
            return CreateInternal(poolSize, [new TestDeferredConsumer(deferredState)], deferredState);
        }

        public static LoopHarness CreateWithNoDeferredConsumers(int poolSize = 8)
            => CreateInternal(poolSize, [], new TestDeferredConsumerState());

        private static LoopHarness CreateInternal(
            int poolSize,
            IDeferredConsumer<TestPayload>[] deferredConsumers,
            TestDeferredConsumerState deferredState)
        {
            var queue = new ProducerConsumerQueue<PipelineEntry<TestPayload>>();
            var payloadPool = new ObjectPool<TestPayload, TestPayload.Allocator>(poolSize);
            var shared = new SharedPipelineState<TestPayload>(queue);
            var clockState = new TestClockState();
            var rendererState = new TestRendererState();
            var clock = new TestRecordingClock(clockState);
            var waiter = new TestNoOpWaiter();
            var producer = new TestWorkerProducer(payloadPool, 1);
            var consumer = new TestRecordingConsumer(rendererState);

            var productionLoop = new ProductionLoop<TestPayload, TestWorkerProducer, TestRecordingClock, TestNoOpWaiter>(
                shared, producer, clock, waiter, TestPipelineConfiguration.Default);
            var consumptionLoop = new ConsumptionLoop<TestPayload, TestRecordingConsumer, TestRecordingClock, TestNoOpWaiter>(
                shared, consumer, clock, waiter, deferredConsumers, TestPipelineConfiguration.Default);

            return new LoopHarness(
                shared,
                clockState,
                deferredState,
                rendererState,
                productionLoop,
                consumptionLoop);
        }

        public sealed class ProductionLoopController(
            LoopHarness owner,
            ProductionLoop<TestPayload, TestWorkerProducer, TestRecordingClock, TestNoOpWaiter>.TestAccessor accessor)
        {
            private readonly LoopHarness _owner = owner;
            private readonly ProductionLoop<TestPayload, TestWorkerProducer, TestRecordingClock, TestNoOpWaiter>.TestAccessor _accessor = accessor;

            public int PinnedPayloadCount => _accessor.PinnedPayloadCount;

            public void Bootstrap()
            {
                _accessor.Bootstrap();
                _owner._productionLastTime = _owner.Clock.NowNanoseconds;
                _owner._productionAccumulator = 0;
            }

            public void RunIteration()
                => _accessor.RunOneIteration(
                    CancellationToken.None,
                    ref _owner._productionLastTime,
                    ref _owner._productionAccumulator);
        }

        public sealed class ConsumptionLoopController(
            ConsumptionLoop<TestPayload, TestRecordingConsumer, TestRecordingClock, TestNoOpWaiter>.TestAccessor accessor)
        {
            private readonly ConsumptionLoop<TestPayload, TestRecordingConsumer, TestRecordingClock, TestNoOpWaiter>.TestAccessor _accessor = accessor;

            public void RunIteration()
                => _accessor.RunOneIteration(CancellationToken.None);
        }

        public sealed class ClockController(TestClockState state)
        {
            private readonly TestClockState _state = state;

            public long NowNanoseconds => _state.NowNanoseconds;

            public void AdvanceBy(long nanoseconds)
                => _state.NowNanoseconds += nanoseconds;
        }

        public sealed class PinController(PinnedVersions pinnedVersions)
        {
            private readonly PinnedVersions _pinnedVersions = pinnedVersions;

            public void Pin(long position, IPinOwner owner)
                => _pinnedVersions.Pin(position, owner);

            public void Unpin(long position, IPinOwner owner)
                => _pinnedVersions.Unpin(position, owner);

            public bool IsPinned(long position)
                => _pinnedVersions.IsPinned(position);
        }

        public sealed class DeferredConsumerController(TestDeferredConsumerState state)
        {
            private readonly TestDeferredConsumerState _state = state;

            public int InFlightCount => _state.InFlightTasks.Count;

            public void FailDispatchWith(Exception exception)
                => _state.ExceptionToThrow = exception;

            public void ClearDispatchFailure()
                => _state.ExceptionToThrow = null;

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

            public IReadOnlyList<long> RenderedPositions => _state.RenderedPositions;

            public void FailWith(Exception exception)
                => _state.ExceptionToThrow = exception;

            public void ClearFailure()
                => _state.ExceptionToThrow = null;
        }
    }

    private sealed class TestDeferredConsumerState
    {
        public readonly List<TaskCompletionSource<long>> InFlightTasks = [];
        public Exception? ExceptionToThrow { get; set; }
    }

    private sealed class TestDeferredConsumer(TestDeferredConsumerState state) : IDeferredConsumer<TestPayload>
    {
        private readonly TestDeferredConsumerState _state = state;

        public long InitialDelayNanoseconds => 0L;

        public long ErrorRetryDelayNanoseconds => 1_000_000_000L;

        public Task<long> ConsumeAsync(TestPayload payload, CancellationToken cancellationToken)
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
        public readonly List<long> RenderedPositions = [];

        public Exception? ExceptionToThrow { get; set; }
    }

    private readonly struct TestRecordingConsumer(TestRendererState state) : IConsumer<TestPayload>
    {
        private readonly TestRendererState _state = state;

        public void Consume(
            in ProducerConsumerQueue<PipelineEntry<TestPayload>>.Segment entries,
            long frameStartNanoseconds,
            CancellationToken cancellationToken)
        {
            if (_state.ExceptionToThrow is Exception exception)
                throw exception;
            _state.RenderedPositions.Add(entries.StartPosition + entries.Count - 1);
        }
    }

    private sealed class ExternalPinOwner : IPinOwner;
}
