using Fabrica.Engine;
using Fabrica.Engine.World;
using Fabrica.Pipeline;
using Fabrica.Pipeline.Memory;
using Fabrica.Pipeline.Threading;
using Fabrica.Tests.Helpers;
using Xunit;

namespace Fabrica.Tests.Engine;

using ChainNode = BaseProductionLoop<WorldImage>.ChainNode;
using ChainNodeAllocator = BaseProductionLoop<WorldImage>.ChainNode.Allocator;

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
    public void RunOneIteration_DoesNotConsumeUntilTwoDistinctNodesExist()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedNode(tick: 12);

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Empty(test.ConsumerState.ConsumedTicks);
        Assert.Equal(0, test.Shared.ConsumptionEpoch);
    }

    [Fact]
    public void RunOneIteration_RendersSnapshot_ThenAdvancesConsumptionEpoch()
    {
        var test = ConsumptionLoopTestContext.Create();
        var first = test.CreatePublishedNode(tick: 11);
        test.Accessor.RunOneIteration(CancellationToken.None);

        var second = test.CreatePublishedNode(tick: 12);
        var epochAtConsume = -1;
        test.ConsumerState.BeforeConsume = (_, _, _) => epochAtConsume = test.Shared.ConsumptionEpoch;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal([12], test.ConsumerState.ConsumedTicks);
        Assert.Equal(0, epochAtConsume);
        Assert.Equal(11, test.Shared.ConsumptionEpoch);
        Assert.Same(second, test.Shared.LatestNode);
    }

    [Fact]
    public void RunOneIteration_ThrottlesOnlyTheRemainingFrameBudget()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedNode(tick: 4);
        test.Accessor.RunOneIteration(CancellationToken.None);
        test.WaiterState.WaitCalls.Clear();

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
        test.CreatePublishedNode(tick: 4);
        test.Accessor.RunOneIteration(CancellationToken.None);
        test.WaiterState.WaitCalls.Clear();

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
        test.CreatePublishedNode(tick: 4);
        test.Accessor.RunOneIteration(CancellationToken.None);
        test.WaiterState.WaitCalls.Clear();

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
        test.CreatePublishedNode(tick: 4);
        var callCount = 0;
        using var cancellationSource = new CancellationTokenSource();
        test.WaiterState.BeforeWait = () =>
        {
            callCount++;
            if (callCount == 1)
            {
                test.CreatePublishedNode(tick: 5);
            }
            else
            {
                cancellationSource.Cancel();
            }
        };

        Assert.Throws<OperationCanceledException>(() => test.Loop.Run(cancellationSource.Token));

        Assert.Equal([5], test.ConsumerState.ConsumedTicks);
    }

    [Fact]
    public void Run_WhenAlreadyCancelled_DoesNothing()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedNode(tick: 4);
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
        test.CreatePublishedNode(tick: 4);
        test.Accessor.RunOneIteration(CancellationToken.None);

        test.CreatePublishedNode(tick: 5);

        test.Accessor.RunOneIteration(CancellationToken.None);
        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal([5, 5], test.ConsumerState.ConsumedTicks);
        Assert.Equal(4, test.Shared.ConsumptionEpoch);
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
        test.ConsumerState.BeforeConsume = (previous, latest, _) =>
        {
            capturedPrevious = previous;
            capturedLatest = latest;
        };

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.NotNull(capturedLatest);
        Assert.NotNull(capturedPrevious);
        Assert.Equal(1, capturedPrevious.SequenceNumber);
        Assert.Equal(6, capturedLatest.SequenceNumber);

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
        test.ConsumerState.BeforeConsume = (previous, latest, _) =>
        {
            capturedPrevious = previous;
            capturedLatest = latest;
        };

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.NotNull(capturedLatest);
        var ticks = new List<int>();
        foreach (var node in ChainNode.Chain(capturedPrevious, capturedLatest))
            ticks.Add(node.SequenceNumber);

        Assert.Equal([0, 1, 2, 3], ticks);
    }

    [Fact]
    public void RunOneIteration_PreviousAndLatestAreAlwaysDistinctReferences()
    {
        var test = ConsumptionLoopTestContext.Create();

        test.CreatePublishedNode(tick: 1);
        test.Accessor.RunOneIteration(CancellationToken.None);
        Assert.Empty(test.ConsumerState.ConsumedTicks);

        test.CreatePublishedNode(tick: 2);
        ChainNode? capturedPrevious = null;
        ChainNode? capturedLatest = null;
        test.ConsumerState.BeforeConsume = (previous, latest, _) =>
        {
            capturedPrevious = previous;
            capturedLatest = latest;
        };
        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.NotNull(capturedLatest);
        Assert.NotNull(capturedPrevious);
        Assert.NotSame(capturedPrevious, capturedLatest);
        Assert.Equal(1, capturedPrevious.SequenceNumber);
        Assert.Equal(2, capturedLatest.SequenceNumber);
    }

    [Fact]
    public void RunOneIteration_EpochProtectsEntireChainFromPreviousToLatest()
    {
        var test = ConsumptionLoopTestContext.Create();

        var tick1 = test.CreatePublishedNode(tick: 1);
        test.Accessor.RunOneIteration(CancellationToken.None);

        var chain = test.CreatePublishedChain(startTick: 2, endTick: 10);
        test.LinkNodes(tick1, chain[0]);

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal(1, test.Shared.ConsumptionEpoch);
    }

    // ── Deferred consumer tests ─────────────────────────────────────────────

    [Fact]
    public void DeferredConsumer_IsNotCalledBeforeItsScheduledTime()
    {
        var deferredState = new TestDeferredConsumerState();
        var test = ConsumptionLoopTestContext.Create(
            deferredConsumers: [new TestDeferredConsumer(deferredState, initialDelayNanoseconds: 1_000_000_000L)]);

        test.CreatePublishedNode(tick: 0);
        test.Accessor.RunOneIteration(CancellationToken.None);

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
            deferredConsumers: [new TestDeferredConsumer(deferredState)]);

        test.CreatePublishedNode(tick: 4);
        test.Accessor.RunOneIteration(CancellationToken.None);

        test.CreatePublishedNode(tick: 5);
        test.ClockState.NowNanoseconds = 200L;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal(1, deferredState._callCount);
        Assert.True(test.PinnedVersions.IsPinned(5));
    }

    [Fact]
    public void DeferredConsumer_Unpins_WhenTaskCompletes()
    {
        var taskCompletionSource = new TaskCompletionSource<long>();
        var deferredState = new TestDeferredConsumerState { _taskToReturn = taskCompletionSource.Task };
        var test = ConsumptionLoopTestContext.Create(
            deferredConsumers: [new TestDeferredConsumer(deferredState)]);

        test.CreatePublishedNode(tick: 2);
        test.Accessor.RunOneIteration(CancellationToken.None);

        test.CreatePublishedNode(tick: 3);
        test.ClockState.NowNanoseconds = 100L;

        test.Accessor.RunOneIteration(CancellationToken.None);
        Assert.True(test.PinnedVersions.IsPinned(3));

        taskCompletionSource.SetResult(long.MaxValue);

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
            deferredConsumers: [new TestDeferredConsumer(deferredState)]);

        test.CreatePublishedNode(tick: 6);
        test.Accessor.RunOneIteration(CancellationToken.None);

        test.CreatePublishedNode(tick: 7);
        test.ClockState.NowNanoseconds = 100L;

        var ex = Assert.Throws<InvalidOperationException>(
            () => test.Accessor.RunOneIteration(CancellationToken.None));

        Assert.Equal("dispatch failed", ex.Message);
        Assert.False(test.PinnedVersions.IsPinned(7));
    }

    [Fact]
    public void RunOneIteration_FrameStartIsNeverBeforeLatestPublishTime()
    {
        var test = ConsumptionLoopTestContext.Create();

        // First iteration: publish node 0 so _latest is set.
        test.ClockState.NowNanoseconds = 50;
        var node0 = test.CreatePublishedNode(tick: 0, publishTimeNanoseconds: 50);
        test.Accessor.RunOneIteration(CancellationToken.None);

        // Second iteration: publish node 1 with PublishTimeNanoseconds = 200, but set the clock to 100 — simulating a race where
        // the production thread published between the clock read and the volatile read of LatestNode.
        test.ClockState.NowNanoseconds = 100;
        var node1 = test.CreatePublishedNode(tick: 1, publishTimeNanoseconds: 200);
        test.LinkNodes(node0, node1);

        long capturedFrameStart = -1;
        test.ConsumerState.BeforeConsume = (_, latest, frameStart) =>
            capturedFrameStart = frameStart;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.True(
            capturedFrameStart >= node1.PublishTimeNanoseconds,
            $"frameStart ({capturedFrameStart}) must be >= latest.PublishTimeNanoseconds ({node1.PublishTimeNanoseconds}). " +
            "A negative elapsed time would corrupt interpolation.");
    }

    private static TimeSpan GetRenderInterval() =>
        TimeSpan.FromTicks(SimulationConstants.RenderIntervalNanoseconds / 100);

    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class TestDeferredConsumerState
    {
        public int _callCount;
        public long _nextRunTime = long.MaxValue;
        public Task<long>? _taskToReturn;
        public Exception? _exceptionToThrow;
    }

    private sealed class TestDeferredConsumer(
        TestDeferredConsumerState state,
        long initialDelayNanoseconds = 0L,
        long errorRetryDelayNanoseconds = 1_000_000_000L) : IDeferredConsumer<WorldImage>
    {
        private readonly TestDeferredConsumerState _state = state;

        public long InitialDelayNanoseconds { get; } = initialDelayNanoseconds;

        public long ErrorRetryDelayNanoseconds { get; } = errorRetryDelayNanoseconds;

        public Task<long> ConsumeAsync(WorldImage payload, CancellationToken cancellationToken)
        {
            _state._callCount++;

            if (_state._exceptionToThrow is { } ex)
                throw ex;

            return _state._taskToReturn ?? Task.FromResult(_state._nextRunTime);
        }
    }

    // ── Test context ────────────────────────────────────────────────────────

    private sealed class TestConsumerState
    {
        public readonly List<int> ConsumedTicks = [];
        public readonly List<(ChainNode Previous, ChainNode Latest)> ConsumedPairs = [];

        public Action<ChainNode, ChainNode, long>? BeforeConsume { get; set; }

        public Exception? ExceptionToThrow { get; set; }
    }

    private readonly struct TestRecordingConsumer(TestConsumerState state) : IConsumer<WorldImage>
    {
        private readonly TestConsumerState _state = state;

        public void Consume(
            ChainNode previous,
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
            IDeferredConsumer<WorldImage>[]? deferredConsumers = null)
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
            IDeferredConsumer<WorldImage>[]? deferredConsumers = null,
            int poolSize = 8)
            where TClock : struct, IClock
            where TWaiter : struct, IWaiter =>
            ConsumptionLoopTestContext<TClock, TWaiter>.Create(
                clock,
                waiter,
                consumerState,
                clockState,
                waiterState,
                deferredConsumers,
                poolSize);
    }

    private sealed class ConsumptionLoopTestContext<TClock, TWaiter>
        where TClock : struct, IClock
        where TWaiter : struct, IWaiter
    {
        private readonly TestChainHarness _harness;
        private readonly ObjectPool<WorldImage, WorldImage.Allocator> _imagePool;
        private readonly BaseProductionLoop<WorldImage>.ChainTestAccessor _chainAccessor;

        public static ConsumptionLoopTestContext<TClock, TWaiter> Create(
            TClock clock,
            TWaiter waiter,
            TestConsumerState consumerState,
            TestClockState clockState,
            TestWaiterState waiterState,
            IDeferredConsumer<WorldImage>[]? deferredConsumers = null,
            int poolSize = 8)
        {
            var nodePool = new ObjectPool<ChainNode, ChainNodeAllocator>(poolSize);
            var imagePool = new ObjectPool<WorldImage, WorldImage.Allocator>(poolSize);
            var shared = new SharedPipelineState<WorldImage>();
            var consumer = new TestRecordingConsumer(consumerState);
            var loop = new ConsumptionLoop<WorldImage, TestRecordingConsumer, TClock, TWaiter>(
                shared,
                consumer,
                clock,
                waiter,
                deferredConsumers ?? [],
                TestPipelineConfiguration.Default);
            var harness = new TestChainHarness(nodePool, shared.PinnedVersions);
            return new ConsumptionLoopTestContext<TClock, TWaiter>(
                harness,
                imagePool,
                shared,
                loop,
                consumerState,
                clockState,
                waiterState);
        }

        private ConsumptionLoopTestContext(
            TestChainHarness harness,
            ObjectPool<WorldImage, WorldImage.Allocator> imagePool,
            SharedPipelineState<WorldImage> shared,
            ConsumptionLoop<WorldImage, TestRecordingConsumer, TClock, TWaiter> loop,
            TestConsumerState consumerState,
            TestClockState clockState,
            TestWaiterState waiterState)
        {
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

        public PinnedVersions PinnedVersions => this.Shared.PinnedVersions;

        public SharedPipelineState<WorldImage> Shared { get; }

        public ConsumptionLoop<WorldImage, TestRecordingConsumer, TClock, TWaiter> Loop { get; }

        public ConsumptionLoop<WorldImage, TestRecordingConsumer, TClock, TWaiter>.TestAccessor Accessor { get; }

        public TestConsumerState ConsumerState { get; }

        public TestClockState ClockState { get; }

        public TestWaiterState WaiterState { get; }

        public ChainNode CreatePublishedNode(int tick) =>
            this.CreatePublishedNode(tick, publishTimeNanoseconds: 0);

        public ChainNode CreatePublishedNode(int tick, long publishTimeNanoseconds)
        {
            var image = _imagePool.Rent();
            var node = _chainAccessor.CreateNode(tick);
            _chainAccessor.SetPayload(node, image);
            _chainAccessor.MarkPublished(node, publishTimeNanoseconds);
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
