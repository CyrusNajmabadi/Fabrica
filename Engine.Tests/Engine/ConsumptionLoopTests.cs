using Engine;
using Engine.Memory;
using Engine.Pipeline;
using Engine.Tests.Helpers;
using Engine.Threading;
using Engine.World;
using Xunit;

namespace Engine.Tests;

using ChainNode = BaseProductionLoop<WorldImage>.ChainNode;
using NodeAllocator = BaseProductionLoop<WorldImage>.NodeAllocator;

public sealed class ConsumptionLoopTests
{
    [Fact]
    public void RunOneIteration_WithNoSnapshot_OnlyThrottles()
    {
        var test = ConsumptionLoopTestContext.Create();

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal(
            [GetRenderInterval()],
            test.WaiterState.WaitCalls);
        Assert.Empty(test.ConsumerState.ConsumedTicks);
        Assert.Equal(0, test.Shared.ConsumptionEpoch);
    }

    [Fact]
    public void RunOneIteration_RendersSnapshot_ThenAdvancesConsumptionEpoch()
    {
        var test = ConsumptionLoopTestContext.Create();
        var node = test.CreatePublishedNode(tick: 12);
        var epochAtConsume = -1;
        test.ConsumerState.BeforeConsume = (_, _, _) => epochAtConsume = test.Shared.ConsumptionEpoch;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal([12], test.ConsumerState.ConsumedTicks);
        Assert.Equal(0, epochAtConsume);
        Assert.Equal(12, test.Shared.ConsumptionEpoch);
        Assert.Equal(
            [GetRenderInterval()],
            test.WaiterState.WaitCalls);
        Assert.Same(node, test.Shared.LatestNode);
    }

    [Fact]
    public void RunOneIteration_ThrottlesOnlyTheRemainingFrameBudget()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedNode(tick: 5);
        test.ClockState.NowNanoseconds = 0;
        test.ConsumerState.BeforeConsume = (_, _, _) => test.ClockState.NowNanoseconds = 5_000_000;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal(
            [TimeSpan.FromTicks((SimulationConstants.RenderIntervalNanoseconds - 5_000_000) / 100)],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_DoesNotThrottleWhenFrameAlreadyExceededBudget()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedNode(tick: 5);
        test.ConsumerState.BeforeConsume =
            (_, _, _) => test.ClockState.NowNanoseconds = SimulationConstants.RenderIntervalNanoseconds + 1;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Empty(test.WaiterState.WaitCalls);
    }

    [Fact]
    public void ThrottleToFrameRate_ClampsNegativeElapsedTimeToZero()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.ClockState.NowNanoseconds = 50;

        test.Accessor.ThrottleToFrameRate(frameStart: 100, CancellationToken.None);

        Assert.Equal(
            [GetRenderInterval()],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_ThrowsWhenCancelledDuringThrottleWait()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedNode(tick: 5);
        using var cancellationSource = new CancellationTokenSource();
        test.WaiterState.BeforeWait = cancellationSource.Cancel;

        Assert.Throws<OperationCanceledException>(
            () => test.Accessor.RunOneIteration(cancellationSource.Token));

        Assert.Equal(
            [GetRenderInterval()],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void Run_ThrowsWhenCancelledDuringThrottleWait()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedNode(tick: 5);
        using var cancellationSource = new CancellationTokenSource();
        test.WaiterState.BeforeWait = cancellationSource.Cancel;

        Assert.Throws<OperationCanceledException>(() => test.Loop.Run(cancellationSource.Token));

        Assert.Equal([5], test.ConsumerState.ConsumedTicks);
    }

    [Fact]
    public void Run_WhenAlreadyCancelled_DoesNothing()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedNode(tick: 5);

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        Assert.Throws<OperationCanceledException>(() => test.Loop.Run(cancellationSource.Token));

        Assert.Empty(test.ConsumerState.ConsumedTicks);
        Assert.Empty(test.WaiterState.WaitCalls);
        Assert.Equal(0, test.Shared.ConsumptionEpoch);
    }

    [Fact]
    public void RepeatedIterations_RenderTheSameLatestSnapshotUntilSimulationPublishesANewerOne()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedNode(tick: 5);

        test.Accessor.RunOneIteration(CancellationToken.None);
        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal([5, 5], test.ConsumerState.ConsumedTicks);
        Assert.Equal(5, test.Shared.ConsumptionEpoch);
    }

    [Fact]
    public void RunOneIteration_WhenSimulationPublishesMultipleTicks_ConsumerReceivesFullChain()
    {
        var test = ConsumptionLoopTestContext.Create();

        var tick1 = test.CreatePublishedNode(tick: 1);
        test.Accessor.RunOneIteration(CancellationToken.None);

        var chain = test.CreatePublishedChain(startTick: 2, endTick: 6);
        test.LinkNodes(tick1, chain[0]);

        ChainNode? capturedPrevious = null;
        ChainNode? capturedLatest = null;
        test.ConsumerState.BeforeConsume = (prev, latest, _) =>
        {
            capturedPrevious = prev;
            capturedLatest = latest;
        };

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.NotNull(capturedLatest);
        Assert.NotNull(capturedPrevious);
        Assert.Equal(1, capturedPrevious!.SequenceNumber);
        Assert.Equal(6, capturedLatest!.SequenceNumber);

        var ticks = new List<int>();
        foreach (var node in ChainNode.Chain(capturedPrevious, capturedLatest))
            ticks.Add(node.SequenceNumber);

        Assert.Equal([1, 2, 3, 4, 5, 6], ticks);
    }

    [Fact]
    public void RunOneIteration_ChainIteratorStopsAtLatest_EvenWhenFurtherNodesExist()
    {
        var test = ConsumptionLoopTestContext.Create();

        var tick0 = test.CreatePublishedNode(tick: 0);
        test.Accessor.RunOneIteration(CancellationToken.None);

        var chain = test.CreatePublishedChain(startTick: 1, endTick: 3);
        test.LinkNodes(tick0, chain[0]);

        var beyondLatest = test.CreateUnpublishedNode(tick: 4);
        test.LinkNodes(chain[^1], beyondLatest);

        ChainNode? capturedPrevious = null;
        ChainNode? capturedLatest = null;
        test.ConsumerState.BeforeConsume = (prev, latest, _) =>
        {
            capturedPrevious = prev;
            capturedLatest = latest;
        };

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.NotNull(capturedLatest);
        var ticks = new List<int>();
        foreach (var node in ChainNode.Chain(capturedPrevious, capturedLatest!))
            ticks.Add(node.SequenceNumber);

        Assert.Equal([0, 1, 2, 3], ticks);
    }

    [Fact]
    public void RunOneIteration_PreviousAndLatestAreAlwaysDistinctReferences_WhenBothNonNull()
    {
        var test = ConsumptionLoopTestContext.Create();

        test.CreatePublishedNode(tick: 1);
        test.Accessor.RunOneIteration(CancellationToken.None);

        ChainNode? previous2 = null;
        ChainNode? latest2 = null;
        test.ConsumerState.BeforeConsume = (prev, latest, _) =>
        {
            previous2 = prev;
            latest2 = latest;
        };
        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.NotNull(latest2);
        Assert.Null(previous2);

        test.CreatePublishedNode(tick: 2);
        ChainNode? previous3 = null;
        ChainNode? latest3 = null;
        test.ConsumerState.BeforeConsume = (prev, latest, _) =>
        {
            previous3 = prev;
            latest3 = latest;
        };
        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.NotNull(latest3);
        Assert.NotNull(previous3);
        Assert.NotSame(previous3, latest3);
        Assert.Equal(1, previous3!.SequenceNumber);
        Assert.Equal(2, latest3!.SequenceNumber);
    }

    [Fact]
    public void RunOneIteration_EpochProtectsEntireChainFromPreviousToLatest()
    {
        var test = ConsumptionLoopTestContext.Create();

        test.CreatePublishedNode(tick: 1);
        test.Accessor.RunOneIteration(CancellationToken.None);

        test.CreatePublishedChain(startTick: 2, endTick: 10);

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal(1, test.Shared.ConsumptionEpoch);
    }

    // ── Deferred consumer tests ─────────────────────────────────────────────

    [Fact]
    public void DeferredConsumer_IsNotCalledBeforeItsScheduledTime()
    {
        var deferredState = new TestDeferredConsumerState();
        var test = ConsumptionLoopTestContext.Create(
            deferredConsumers: [new DeferredConsumerRegistration<WorldImage>(
                new TestDeferredConsumer(deferredState), 1_000_000_000L)]);

        test.CreatePublishedNode(tick: 1);
        test.ClockState.NowNanoseconds = 500_000_000L;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal(0, deferredState._callCount);
    }

    [Fact]
    public void DeferredConsumer_IsCalledWhenDue_AndPinsNode()
    {
        var deferredState = new TestDeferredConsumerState { _nextRunTime = long.MaxValue };
        var test = ConsumptionLoopTestContext.Create(
            deferredConsumers: [new DeferredConsumerRegistration<WorldImage>(
                new TestDeferredConsumer(deferredState), 0L)]);

        test.CreatePublishedNode(tick: 5);
        test.ClockState.NowNanoseconds = 200L;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal(1, deferredState._callCount);
        Assert.Equal(5, deferredState._lastSequenceNumber);
        Assert.True(test.PinnedVersions.IsPinned(5));
    }

    [Fact]
    public void DeferredConsumer_Unpins_WhenTaskCompletes()
    {
        var tcs = new TaskCompletionSource<long>();
        var deferredState = new TestDeferredConsumerState { _taskToReturn = tcs.Task };
        var test = ConsumptionLoopTestContext.Create(
            deferredConsumers: [new DeferredConsumerRegistration<WorldImage>(
                new TestDeferredConsumer(deferredState), 0L)]);

        test.CreatePublishedNode(tick: 3);
        test.ClockState.NowNanoseconds = 100L;

        test.Accessor.RunOneIteration(CancellationToken.None);
        Assert.True(test.PinnedVersions.IsPinned(3));

        tcs.SetResult(long.MaxValue);

        test.Accessor.RunOneIteration(CancellationToken.None);
        Assert.False(test.PinnedVersions.IsPinned(3));
    }

    [Fact]
    public void DeferredConsumer_DispatchFailure_UnpinsImmediately()
    {
        var deferredState = new TestDeferredConsumerState
        {
            _exceptionToThrow = new InvalidOperationException("dispatch failed"),
        };
        var test = ConsumptionLoopTestContext.Create(
            deferredConsumers: [new DeferredConsumerRegistration<WorldImage>(
                new TestDeferredConsumer(deferredState), 0L)]);

        test.CreatePublishedNode(tick: 7);
        test.ClockState.NowNanoseconds = 100L;

        var ex = Assert.Throws<InvalidOperationException>(
            () => test.Accessor.RunOneIteration(CancellationToken.None));

        Assert.Equal("dispatch failed", ex.Message);
        Assert.False(test.PinnedVersions.IsPinned(7));
    }

    private static TimeSpan GetRenderInterval() =>
        TimeSpan.FromTicks(SimulationConstants.RenderIntervalNanoseconds / 100);

    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class TestDeferredConsumerState
    {
        public int _callCount;
        public int _lastSequenceNumber;
        public long _nextRunTime = long.MaxValue;
        public Task<long>? _taskToReturn;
        public Exception? _exceptionToThrow;
    }

    private sealed class TestDeferredConsumer : IDeferredConsumer<WorldImage>
    {
        private readonly TestDeferredConsumerState _state;

        public TestDeferredConsumer(TestDeferredConsumerState state) => _state = state;

        public Task<long> ConsumeAsync(WorldImage payload, int sequenceNumber, CancellationToken cancellationToken)
        {
            _state._callCount++;
            _state._lastSequenceNumber = sequenceNumber;

            if (_state._exceptionToThrow is { } ex)
                throw ex;

            return _state._taskToReturn ?? Task.FromResult(_state._nextRunTime);
        }
    }

    // ── Test context ────────────────────────────────────────────────────────

    private sealed class TestConsumerState
    {
        public readonly List<int> ConsumedTicks = [];
        public readonly List<(ChainNode? Previous, ChainNode Latest)> ConsumedPairs = [];

        public Action<ChainNode?, ChainNode, long>? BeforeConsume { get; set; }

        public Exception? ExceptionToThrow { get; set; }
    }

    private readonly struct TestRecordingConsumer : IConsumer<WorldImage>
    {
        private readonly TestConsumerState _state;

        public TestRecordingConsumer(TestConsumerState state) => _state = state;

        public void Consume(
            ChainNode? previous,
            ChainNode latest,
            long frameStartNanoseconds,
            CancellationToken cancellationToken)
        {
            _state.BeforeConsume?.Invoke(previous, latest, frameStartNanoseconds);
            if (_state.ExceptionToThrow is Exception exception)
                throw exception;
            _state.ConsumedTicks.Add(latest.SequenceNumber);
            _state.ConsumedPairs.Add((previous, latest));
        }
    }

    private static class ConsumptionLoopTestContext
    {
        public static ConsumptionLoopTestContext<TestRecordingClock, TestRecordingWaiter> Create(
            DeferredConsumerRegistration<WorldImage>[]? deferredConsumers = null)
        {
            var clockState = new TestClockState();
            var waiterState = new TestWaiterState();
            var consumerState = new TestConsumerState();

            return Create(
                clock: new TestRecordingClock(clockState),
                waiter: new TestRecordingWaiter(waiterState),
                consumerState: consumerState,
                clockState: clockState,
                waiterState: waiterState,
                deferredConsumers: deferredConsumers ?? []);
        }

        public static ConsumptionLoopTestContext<TClock, TWaiter> Create<TClock, TWaiter>(
            TClock clock,
            TWaiter waiter,
            TestConsumerState consumerState,
            TestClockState clockState,
            TestWaiterState waiterState,
            DeferredConsumerRegistration<WorldImage>[]? deferredConsumers = null,
            int poolSize = 8)
            where TClock : struct, IClock
            where TWaiter : struct, IWaiter
        {
            var pinnedVersions = new PinnedVersions();
            var nodePool = new ObjectPool<ChainNode, NodeAllocator>(poolSize);
            var imagePool = new ObjectPool<WorldImage, WorldImageAllocator>(poolSize);
            var shared = new SharedState<WorldImage>();
            var consumer = new TestRecordingConsumer(consumerState);
            var loop = new ConsumptionLoop<WorldImage, TestRecordingConsumer, TClock, TWaiter>(
                pinnedVersions,
                shared,
                consumer,
                clock,
                waiter,
                deferredConsumers ?? []);
            var harness = new TestChainHarness(nodePool, pinnedVersions);
            return new ConsumptionLoopTestContext<TClock, TWaiter>(
                pinnedVersions,
                harness,
                imagePool,
                shared,
                loop,
                consumerState,
                clockState,
                waiterState);
        }
    }

    private sealed class ConsumptionLoopTestContext<TClock, TWaiter>
        where TClock : struct, IClock
        where TWaiter : struct, IWaiter
    {
        private readonly TestChainHarness _harness;
        private readonly ObjectPool<WorldImage, WorldImageAllocator> _imagePool;
        private readonly BaseProductionLoop<WorldImage>.ChainTestAccessor _chainAccessor;

        internal ConsumptionLoopTestContext(
            PinnedVersions pinnedVersions,
            TestChainHarness harness,
            ObjectPool<WorldImage, WorldImageAllocator> imagePool,
            SharedState<WorldImage> shared,
            ConsumptionLoop<WorldImage, TestRecordingConsumer, TClock, TWaiter> loop,
            TestConsumerState consumerState,
            TestClockState clockState,
            TestWaiterState waiterState)
        {
            this.PinnedVersions = pinnedVersions;
            _harness = harness;
            _imagePool = imagePool;
            _chainAccessor = harness.GetChainTestAccessor();
            this.Shared = shared;
            this.Loop = loop;
            this.Accessor = loop.GetTestAccessor();
            this.ConsumerState = consumerState;
            this.ClockState = clockState;
            this.WaiterState = waiterState;
        }

        public PinnedVersions PinnedVersions { get; }

        public SharedState<WorldImage> Shared { get; }

        public ConsumptionLoop<WorldImage, TestRecordingConsumer, TClock, TWaiter> Loop { get; }

        public ConsumptionLoop<WorldImage, TestRecordingConsumer, TClock, TWaiter>.TestAccessor Accessor { get; }

        public TestConsumerState ConsumerState { get; }

        public TestClockState ClockState { get; }

        public TestWaiterState WaiterState { get; }

        public ChainNode CreatePublishedNode(int tick)
        {
            var image = _imagePool.Rent();
            var node = _chainAccessor.CreateNode(tick);
            _chainAccessor.SetPayload(node, image);
            this.Shared.LatestNode = node;
            return node;
        }

        public ChainNode CreateUnpublishedNode(int tick)
        {
            var image = _imagePool.Rent();
            var node = _chainAccessor.CreateNode(tick);
            _chainAccessor.SetPayload(node, image);
            return node;
        }

        public ChainNode[] CreatePublishedChain(int startTick, int endTick)
        {
            var count = endTick - startTick + 1;
            var chain = new ChainNode[count];
            for (var i = 0; i < count; i++)
            {
                var image = _imagePool.Rent();
                var node = _chainAccessor.CreateNode(startTick + i);
                _chainAccessor.SetPayload(node, image);
                if (i > 0)
                    _chainAccessor.LinkNodes(chain[i - 1], node);
                chain[i] = node;
            }

            this.Shared.LatestNode = chain[^1];
            return chain;
        }

        public void LinkNodes(ChainNode current, ChainNode next) =>
            _chainAccessor.LinkNodes(current, next);
    }
}
