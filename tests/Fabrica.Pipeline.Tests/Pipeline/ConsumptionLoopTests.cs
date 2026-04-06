using Fabrica.Core.Collections;
using Fabrica.Core.Memory;
using Fabrica.Core.Threading;
using Fabrica.Pipeline.Tests.Helpers;
using Xunit;

namespace Fabrica.Pipeline.Tests.Pipeline;

public sealed class ConsumptionLoopTests
{
    [Fact]
    public void RunOneIteration_WithNoEntries_OnlyThrottles()
    {
        var test = ConsumptionLoopTestContext.Create();

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal(
            [GetRenderInterval()],
            test.WaiterState.WaitCalls);
        Assert.Empty(test.ConsumerState.ConsumedLatestPositions);
        Assert.Equal(0, test.Shared.Queue.ConsumerPosition);
    }

    [Fact]
    public void RunOneIteration_DoesNotConsumeUntilTwoEntriesExist()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.AppendEntry();

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Empty(test.ConsumerState.ConsumedLatestPositions);
        Assert.Equal(0, test.Shared.Queue.ConsumerPosition);
    }

    [Fact]
    public void RunOneIteration_ConsumesAndAdvancesConsumerPosition()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.AppendEntry();
        test.Accessor.RunOneIteration(CancellationToken.None);

        test.AppendEntry();
        test.ConsumerState.BeforeConsume = _ =>
            Assert.Equal(0, test.Shared.Queue.ConsumerPosition);

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal([1], test.ConsumerState.ConsumedLatestPositions);
        Assert.Equal(1, test.Shared.Queue.ConsumerPosition);
        Assert.Equal(0, test.ConsumerState.LastFirstTick);
        Assert.Equal(1, test.ConsumerState.LastLatestTick);
    }

    [Fact]
    public void RunOneIteration_ThrottlesOnlyTheRemainingFrameBudget()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.AppendEntry();
        test.Accessor.RunOneIteration(CancellationToken.None);
        test.WaiterState.WaitCalls.Clear();

        test.AppendEntry();
        test.ClockState.NowNanoseconds = 0;
        test.ConsumerState.BeforeConsume = _ => test.ClockState.NowNanoseconds = 5_000_000;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal(
            [TimeSpan.FromTicks((TestPipelineConfiguration.RenderIntervalNanoseconds - 5_000_000) / 100)],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_DoesNotThrottleWhenFrameAlreadyExceededBudget()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.AppendEntry();
        test.Accessor.RunOneIteration(CancellationToken.None);
        test.WaiterState.WaitCalls.Clear();

        test.AppendEntry();
        test.ConsumerState.BeforeConsume =
            _ => test.ClockState.NowNanoseconds = TestPipelineConfiguration.RenderIntervalNanoseconds + 1;

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
        test.AppendEntry();
        test.Accessor.RunOneIteration(CancellationToken.None);
        test.WaiterState.WaitCalls.Clear();

        test.AppendEntry();
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
        test.AppendEntry();
        var callCount = 0;
        using var cancellationSource = new CancellationTokenSource();
        test.WaiterState.BeforeWait = () =>
        {
            callCount++;
            if (callCount == 1)
            {
                test.AppendEntry();
            }
            else
            {
                cancellationSource.Cancel();
            }
        };

        Assert.Throws<OperationCanceledException>(() => test.Loop.Run(cancellationSource.Token));

        Assert.Equal([1], test.ConsumerState.ConsumedLatestPositions);
    }

    [Fact]
    public void Run_WhenAlreadyCancelled_DoesNothing()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.AppendEntry();
        test.AppendEntry();

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        Assert.Throws<OperationCanceledException>(() => test.Loop.Run(cancellationSource.Token));

        Assert.Empty(test.ConsumerState.ConsumedLatestPositions);
        Assert.Empty(test.WaiterState.WaitCalls);
        Assert.Equal(0, test.Shared.Queue.ConsumerPosition);
    }

    [Fact]
    public void RunOneIteration_DoesNotReconsumeWithoutNewEntries()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.AppendEntry();
        test.Accessor.RunOneIteration(CancellationToken.None);

        test.AppendEntry();

        test.Accessor.RunOneIteration(CancellationToken.None);
        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal([1], test.ConsumerState.ConsumedLatestPositions);
        Assert.Equal(1, test.Shared.Queue.ConsumerPosition);
    }

    [Fact]
    public void RunOneIteration_WhenSimulationPublishesMultipleEntries_ConsumerReceivesFullSegment()
    {
        var test = ConsumptionLoopTestContext.Create();

        test.AppendEntry();
        test.Accessor.RunOneIteration(CancellationToken.None);

        for (var i = 0; i < 5; i++)
            test.AppendEntry();

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal(0, test.ConsumerState.LastSegmentStart);
        Assert.Equal(6, test.ConsumerState.LastSegmentCount);
        Assert.Equal([5], test.ConsumerState.ConsumedLatestPositions);
        Assert.Equal(0, test.ConsumerState.LastFirstTick);
        Assert.Equal(5, test.ConsumerState.LastLatestTick);
    }

    [Fact]
    public void RunOneIteration_FirstAndLastEntriesAreAlwaysDistinct()
    {
        var test = ConsumptionLoopTestContext.Create();

        test.AppendEntry();
        test.Accessor.RunOneIteration(CancellationToken.None);
        Assert.Empty(test.ConsumerState.ConsumedLatestPositions);

        test.AppendEntry();
        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.NotNull(test.ConsumerState.LastLatestPayload);
        Assert.NotNull(test.ConsumerState.LastFirstPayload);
        Assert.NotSame(test.ConsumerState.LastFirstPayload, test.ConsumerState.LastLatestPayload);
        Assert.Equal(0, test.ConsumerState.LastSegmentStart);
        Assert.Equal(2, test.ConsumerState.LastSegmentCount);
    }

    [Fact]
    public void RunOneIteration_ConsumerAdvancesCorrectlyAfterMultipleEntries()
    {
        var test = ConsumptionLoopTestContext.Create();

        test.AppendEntry();
        test.Accessor.RunOneIteration(CancellationToken.None);

        for (var i = 0; i < 9; i++)
            test.AppendEntry();

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal(9, test.Shared.Queue.ConsumerPosition);
    }

    // ── Deferred consumer tests ─────────────────────────────────────────────

    [Fact]
    public void DeferredConsumer_IsNotCalledBeforeItsScheduledTime()
    {
        var deferredState = new TestDeferredConsumerState();
        var test = ConsumptionLoopTestContext.Create(
            deferredConsumers: [new TestDeferredConsumer(deferredState, initialDelayNanoseconds: 1_000_000_000L)]);

        test.AppendEntry();
        test.Accessor.RunOneIteration(CancellationToken.None);

        test.AppendEntry();
        test.ClockState.NowNanoseconds = 500_000_000L;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal(0, deferredState.CallCount);
    }

    [Fact]
    public void DeferredConsumer_IsCalledWhenDue_AndPinsPosition()
    {
        var deferredState = new TestDeferredConsumerState { NextRunTime = long.MaxValue };
        var test = ConsumptionLoopTestContext.Create(
            deferredConsumers: [new TestDeferredConsumer(deferredState)]);

        test.AppendEntry();
        test.Accessor.RunOneIteration(CancellationToken.None);

        test.AppendEntry();
        test.ClockState.NowNanoseconds = 200L;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal(1, deferredState.CallCount);
        Assert.True(test.PinnedVersions.IsPinned(1));
    }

    [Fact]
    public void DeferredConsumer_Unpins_WhenTaskCompletes()
    {
        var taskCompletionSource = new TaskCompletionSource<long>();
        var deferredState = new TestDeferredConsumerState { TaskToReturn = taskCompletionSource.Task };
        var test = ConsumptionLoopTestContext.Create(
            deferredConsumers: [new TestDeferredConsumer(deferredState)]);

        test.AppendEntry();
        test.Accessor.RunOneIteration(CancellationToken.None);

        test.AppendEntry();
        test.ClockState.NowNanoseconds = 100L;

        test.Accessor.RunOneIteration(CancellationToken.None);
        Assert.True(test.PinnedVersions.IsPinned(1));

        taskCompletionSource.SetResult(long.MaxValue);

        test.Accessor.RunOneIteration(CancellationToken.None);
        Assert.False(test.PinnedVersions.IsPinned(1));
    }

    [Fact]
    public void DeferredConsumer_DispatchFailure_UnpinsImmediately()
    {
        var deferredState = new TestDeferredConsumerState
        {
            ExceptionToThrow = new InvalidOperationException("dispatch failed"),
        };
        var test = ConsumptionLoopTestContext.Create(
            deferredConsumers: [new TestDeferredConsumer(deferredState)]);

        test.AppendEntry();
        test.Accessor.RunOneIteration(CancellationToken.None);

        test.AppendEntry();
        test.ClockState.NowNanoseconds = 100L;

        var ex = Assert.Throws<InvalidOperationException>(
            () => test.Accessor.RunOneIteration(CancellationToken.None));

        Assert.Equal("dispatch failed", ex.Message);
        Assert.False(test.PinnedVersions.IsPinned(1));
    }

    [Fact]
    public void DeferredConsumer_FaultedTask_UnpinsAndReschedulesWithErrorRetryDelay()
    {
        var taskCompletionSource = new TaskCompletionSource<long>();
        var deferredState = new TestDeferredConsumerState { TaskToReturn = taskCompletionSource.Task };
        const long ErrorRetryDelay = 500_000_000L;
        var test = ConsumptionLoopTestContext.Create(
            deferredConsumers: [new TestDeferredConsumer(deferredState, errorRetryDelayNanoseconds: ErrorRetryDelay)]);

        test.AppendEntry();
        test.Accessor.RunOneIteration(CancellationToken.None);

        test.AppendEntry();
        test.ClockState.NowNanoseconds = 100L;

        test.Accessor.RunOneIteration(CancellationToken.None);
        Assert.Equal(1, deferredState.CallCount);
        Assert.True(test.PinnedVersions.IsPinned(1));

        taskCompletionSource.SetException(new InvalidOperationException("save failed"));

        test.ClockState.NowNanoseconds = 200L;
        test.Accessor.RunOneIteration(CancellationToken.None);
        Assert.False(test.PinnedVersions.IsPinned(1));

        // The consumer should be rescheduled at frameStart + ErrorRetryDelay = 200 + 500_000_000.
        // Running at a time before that should NOT re-trigger the consumer.
        test.AppendEntry();
        test.ClockState.NowNanoseconds = 1000L;
        test.Accessor.RunOneIteration(CancellationToken.None);
        Assert.Equal(1, deferredState.CallCount);

        // Running past the retry delay should trigger it again.
        deferredState.TaskToReturn = null;
        deferredState.NextRunTime = long.MaxValue;
        test.AppendEntry();
        test.ClockState.NowNanoseconds = 200L + ErrorRetryDelay + 1;
        test.Accessor.RunOneIteration(CancellationToken.None);
        Assert.Equal(2, deferredState.CallCount);
    }

    [Fact]
    public void RunOneIteration_FrameStartIsNeverBeforeLatestPublishTime()
    {
        var test = ConsumptionLoopTestContext.Create();

        test.ClockState.NowNanoseconds = 50;
        test.AppendEntry(publishTimeNanoseconds: 50);
        test.Accessor.RunOneIteration(CancellationToken.None);

        test.ClockState.NowNanoseconds = 100;
        test.AppendEntry(publishTimeNanoseconds: 200);

        long capturedFrameStart = -1;
        test.ConsumerState.BeforeConsume = frameStart =>
            capturedFrameStart = frameStart;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.True(
            capturedFrameStart >= 200,
            $"frameStart ({capturedFrameStart}) must be >= latest PublishTimeNanoseconds (200). " +
            "A negative elapsed time would corrupt interpolation.");
    }

    private static TimeSpan GetRenderInterval()
        => TimeSpan.FromTicks(TestPipelineConfiguration.RenderIntervalNanoseconds / 100);

    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class TestDeferredConsumerState
    {
        public int CallCount;
        public long NextRunTime = long.MaxValue;
        public Task<long>? TaskToReturn;
        public Exception? ExceptionToThrow;
    }

    private sealed class TestDeferredConsumer(
        TestDeferredConsumerState state,
        long initialDelayNanoseconds = 0L,
        long errorRetryDelayNanoseconds = 1_000_000_000L) : IDeferredConsumer<TestPayload>
    {
        private readonly TestDeferredConsumerState _state = state;

        public long InitialDelayNanoseconds { get; } = initialDelayNanoseconds;

        public long ErrorRetryDelayNanoseconds { get; } = errorRetryDelayNanoseconds;

        public Task<long> ConsumeAsync(TestPayload payload, CancellationToken cancellationToken)
        {
            _state.CallCount++;

            if (_state.ExceptionToThrow is { } ex)
                throw ex;

            return _state.TaskToReturn ?? Task.FromResult(_state.NextRunTime);
        }
    }

    // ── Test context ────────────────────────────────────────────────────────

    private sealed class TestConsumerState
    {
        public readonly List<long> ConsumedLatestPositions = [];
        public long LastSegmentStart { get; set; }
        public long LastSegmentCount { get; set; }
        public TestPayload? LastFirstPayload { get; set; }
        public TestPayload? LastLatestPayload { get; set; }
        public long LastLatestPublishTimeNanoseconds { get; set; }
        public long LastFirstTick { get; set; }
        public long LastLatestTick { get; set; }

        public Action<long>? BeforeConsume { get; set; }

        public Exception? ExceptionToThrow { get; set; }
    }

    private readonly struct TestRecordingConsumer(TestConsumerState state) : IConsumer<TestPayload>
    {
        private readonly TestConsumerState _state = state;

        public void Consume(
            in ProducerConsumerQueue<PipelineEntry<TestPayload>>.Segment entries,
            long frameStartNanoseconds,
            CancellationToken cancellationToken)
        {
            _state.LastSegmentStart = entries.StartPosition;
            _state.LastSegmentCount = entries.Count;
            var firstEntry = entries[0];
            _state.LastFirstPayload = firstEntry.Payload;
            _state.LastFirstTick = firstEntry.Tick;
            var latestEntry = entries[^1];
            _state.LastLatestPayload = latestEntry.Payload;
            _state.LastLatestTick = latestEntry.Tick;
            _state.LastLatestPublishTimeNanoseconds = latestEntry.PublishTimeNanoseconds;

            _state.BeforeConsume?.Invoke(frameStartNanoseconds);
            if (_state.ExceptionToThrow is Exception exception)
                throw exception;
            _state.ConsumedLatestPositions.Add(entries.StartPosition + entries.Count - 1);
        }
    }

    private static class ConsumptionLoopTestContext
    {
        public static ConsumptionLoopTestContext<TestRecordingClock, TestRecordingWaiter> Create(
            IDeferredConsumer<TestPayload>[]? deferredConsumers = null)
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
            IDeferredConsumer<TestPayload>[]? deferredConsumers = null,
            int poolSize = 8)
            where TClock : struct, IClock
            where TWaiter : struct, IWaiter
            => ConsumptionLoopTestContext<TClock, TWaiter>.Create(
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
        private readonly ObjectPool<TestPayload, TestPayload.Allocator> _payloadPool;

        public static ConsumptionLoopTestContext<TClock, TWaiter> Create(
            TClock clock,
            TWaiter waiter,
            TestConsumerState consumerState,
            TestClockState clockState,
            TestWaiterState waiterState,
            IDeferredConsumer<TestPayload>[]? deferredConsumers = null,
            int poolSize = 8)
        {
            var queue = new ProducerConsumerQueue<PipelineEntry<TestPayload>>();
            var payloadPool = new ObjectPool<TestPayload, TestPayload.Allocator>(poolSize);
            var shared = new SharedPipelineState<TestPayload>(queue);
            var consumer = new TestRecordingConsumer(consumerState);
            var loop = new ConsumptionLoop<TestPayload, TestRecordingConsumer, TClock, TWaiter>(
                shared,
                consumer,
                clock,
                waiter,
                deferredConsumers ?? [],
                TestPipelineConfiguration.Default);
            return new ConsumptionLoopTestContext<TClock, TWaiter>(
                payloadPool,
                shared,
                loop,
                consumerState,
                clockState,
                waiterState);
        }

        private ConsumptionLoopTestContext(
            ObjectPool<TestPayload, TestPayload.Allocator> payloadPool,
            SharedPipelineState<TestPayload> shared,
            ConsumptionLoop<TestPayload, TestRecordingConsumer, TClock, TWaiter> loop,
            TestConsumerState consumerState,
            TestClockState clockState,
            TestWaiterState waiterState)
        {
            _payloadPool = payloadPool;
            this.Shared = shared;
            this.Loop = loop;
            this.Accessor = loop.GetTestAccessor();
            this.ConsumerState = consumerState;
            this.ClockState = clockState;
            this.WaiterState = waiterState;
        }

        public PinnedVersions PinnedVersions => this.Shared.PinnedVersions;

        public SharedPipelineState<TestPayload> Shared { get; }

        public ConsumptionLoop<TestPayload, TestRecordingConsumer, TClock, TWaiter> Loop { get; }

        public ConsumptionLoop<TestPayload, TestRecordingConsumer, TClock, TWaiter>.TestAccessor Accessor { get; }

        public TestConsumerState ConsumerState { get; }

        public TestClockState ClockState { get; }

        public TestWaiterState WaiterState { get; }

        private long _nextTick;

        public void AppendEntry(long publishTimeNanoseconds = 0)
        {
            var payload = _payloadPool.Rent();
            this.Shared.Queue.ProducerAppend(new PipelineEntry<TestPayload>
            {
                Payload = payload,
                Tick = _nextTick++,
                PublishTimeNanoseconds = publishTimeNanoseconds,
            });
        }
    }
}
