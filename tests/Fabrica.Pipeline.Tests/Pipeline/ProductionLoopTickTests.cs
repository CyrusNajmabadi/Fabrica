using Fabrica.Core.Collections;
using Fabrica.Core.Memory;
using Fabrica.Core.Threading;
using Fabrica.Pipeline.Tests.Helpers;
using Xunit;

namespace Fabrica.Pipeline.Tests.Pipeline;

using SimulationPressure = ProductionLoop<TestPayload, TestWorkerProducer, TestFakeClock, TestRecordingWaiter>.SimulationPressure;

public sealed class ProductionLoopTickTests
{
    private static int LowWaterMarkTicks =>
        (int)(TestPipelineConfiguration.PressureLowWaterMarkNanoseconds / TestPipelineConfiguration.TickDurationNanoseconds);

    private static int HardCeilingTicks =>
        (int)(TestPipelineConfiguration.PressureHardCeilingNanoseconds / TestPipelineConfiguration.TickDurationNanoseconds);

    [Fact]
    public void Bootstrap_PublishesInitialEntry()
    {
        var test = ProductionLoopTestContext.Create();

        test.Accessor.Bootstrap();

        Assert.NotNull(test.Accessor.CurrentPayload);
        Assert.Equal(1, test.Shared.Queue.ProducerPosition);
        Assert.Equal(0, test.Shared.Queue.ConsumerPosition);

        var segment = test.Shared.Queue.ConsumerAcquire();
        Assert.Equal(0, segment[0].Tick);
    }

    [Fact]
    public void Tick_PublishesNextEntry()
    {
        var test = ProductionLoopTestContext.Create();

        test.Accessor.Bootstrap();
        var initialPayload = test.Accessor.CurrentPayload;

        test.Accessor.Tick();

        Assert.Equal(2, test.Shared.Queue.ProducerPosition);
        Assert.NotSame(initialPayload, test.Accessor.CurrentPayload);

        var segment = test.Shared.Queue.ConsumerAcquire();
        Assert.Equal(0, segment[0].Tick);
        Assert.Equal(1, segment[^1].Tick);
    }

    [Fact]
    public void Tick_StampsMonotonicallyIncreasingTickValues()
    {
        var test = ProductionLoopTestContext.Create();

        test.Accessor.Bootstrap();
        for (var i = 0; i < 5; i++)
            test.Accessor.Tick();

        var segment = test.Shared.Queue.ConsumerAcquire();
        Assert.Equal(6, segment.Count);
        for (var i = 0; i < (int)segment.Count; i++)
            Assert.Equal(i, segment[i].Tick);
    }

    [Fact]
    public void Cleanup_FreesUnpinnedEntriesPassedByConsumer()
    {
        var test = ProductionLoopTestContext.Create();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();

        test.Shared.Queue.ConsumerAdvance(2);

        test.Accessor.Cleanup();

        Assert.Equal(0, test.Accessor.PinnedPayloadCount);
    }

    [Fact]
    public void Cleanup_WithNoConsumerAdvance_DoesNothing()
    {
        var test = ProductionLoopTestContext.Create();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();

        test.Accessor.Cleanup();

        Assert.Equal(0, test.Accessor.PinnedPayloadCount);
        Assert.NotNull(test.Accessor.CurrentPayload);
    }

    [Fact]
    public void Cleanup_AtConsumerBoundary_FreesOnlyPassedEntries()
    {
        var test = ProductionLoopTestContext.Create();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();

        test.Shared.Queue.ConsumerAdvance(1);

        test.Accessor.Cleanup();

        Assert.Equal(0, test.Accessor.PinnedPayloadCount);
    }

    [Fact]
    public void Cleanup_StashesPinnedPayloadsUntilTheyAreUnpinned()
    {
        var test = ProductionLoopTestContext.Create();
        var pinOwner = new TestPinOwner();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();

        test.PinnedVersions.Pin(0, pinOwner);
        test.Shared.Queue.ConsumerAdvance(2);

        test.Accessor.Cleanup();

        Assert.Equal(1, test.Accessor.PinnedPayloadCount);

        test.PinnedVersions.Unpin(0, pinOwner);

        test.Accessor.Cleanup();

        Assert.Equal(0, test.Accessor.PinnedPayloadCount);
    }

    [Fact]
    public void Cleanup_PinnedMiddleEntry_DoesNotBlockLaterEntriesFromBeingFreed()
    {
        var test = ProductionLoopTestContext.Create();
        var pinOwner = new TestPinOwner();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();
        test.Accessor.Tick();

        test.PinnedVersions.Pin(1, pinOwner);
        test.Shared.Queue.ConsumerAdvance(3);

        test.Accessor.Cleanup();

        Assert.Equal(1, test.Accessor.PinnedPayloadCount);

        test.PinnedVersions.Unpin(1, pinOwner);
        test.Accessor.Cleanup();

        Assert.Equal(0, test.Accessor.PinnedPayloadCount);
    }

    // ── RunOneIteration — basic ──────────────────────────────────────────────

    [Fact]
    public void RunOneIteration_DoesNotTick_WhenAccumulatorIsBelowThreshold()
    {
        var test = ProductionLoopTestContext.Create();

        test.Accessor.Bootstrap();

        long lastTime = 0;
        var accumulator = TestPipelineConfiguration.TickDurationNanoseconds - 1;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(1, test.Shared.Queue.ProducerPosition);
        Assert.Equal(TestPipelineConfiguration.TickDurationNanoseconds - 1, accumulator);
        Assert.Equal(
            [GetIdleYieldWait()],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_TicksOnce_WhenAccumulatorReachesThreshold()
    {
        var test = ProductionLoopTestContext.Create();

        test.Accessor.Bootstrap();

        long lastTime = 0;
        var accumulator = TestPipelineConfiguration.TickDurationNanoseconds;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(2, test.Shared.Queue.ProducerPosition);
        Assert.Equal(0, accumulator);
        Assert.Equal(
            [GetIdleYieldWait()],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_ThrowsWhenCancelledDuringIdleWait()
    {
        var test = ProductionLoopTestContext.Create();

        test.Accessor.Bootstrap();

        long lastTime = 0;
        long accumulator = 0;

        using var cancellationSource = new CancellationTokenSource();
        test.WaiterState.BeforeWait = cancellationSource.Cancel;

        Assert.Throws<OperationCanceledException>(
            () => test.Accessor.RunOneIteration(cancellationSource.Token, ref lastTime, ref accumulator));

        Assert.Equal(1, test.Shared.Queue.ProducerPosition);
        Assert.Equal(
            [GetIdleYieldWait()],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_ProcessesMultipleTicks_AndPreservesLeftoverAccumulator()
    {
        var test = ProductionLoopTestContext.Create();

        test.Accessor.Bootstrap();

        long lastTime = 0;
        var accumulator = (TestPipelineConfiguration.TickDurationNanoseconds * 2) + 123;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(3, test.Shared.Queue.ProducerPosition);
        Assert.Equal(123, accumulator);
        Assert.Equal(
            [GetIdleYieldWait()],
            test.WaiterState.WaitCalls);
    }

    // ── RunOneIteration — soft pressure ──────────────────────────────────────

    [Fact]
    public void RunOneIteration_AppliesPressureDelay_WhenGapExceedsLowWaterMark()
    {
        var test = ProductionLoopTestContext.Create();

        test.Accessor.Bootstrap();
        for (var i = 0; i < LowWaterMarkTicks + 1; i++)
            test.Accessor.Tick();

        test.WaiterState.WaitCalls.Clear();

        long lastTime = 0;
        var accumulator = TestPipelineConfiguration.TickDurationNanoseconds;
        var positionsBeforeTick = test.Shared.Queue.ProducerPosition;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(positionsBeforeTick + 1, test.Shared.Queue.ProducerPosition);
        Assert.Equal(
            [
                GetExpectedPressureDelay(positionsBeforeTick, test.Shared.Queue.ConsumerPosition),
                GetIdleYieldWait(),
            ],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_ThrowsWhenCancelledDuringPressureDelay()
    {
        var test = ProductionLoopTestContext.Create();

        test.Accessor.Bootstrap();
        for (var i = 0; i < LowWaterMarkTicks + 1; i++)
            test.Accessor.Tick();

        var positionBefore = test.Shared.Queue.ProducerPosition;
        test.WaiterState.WaitCalls.Clear();

        long lastTime = 0;
        var accumulator = TestPipelineConfiguration.TickDurationNanoseconds;

        using var cancellationSource = new CancellationTokenSource();
        test.WaiterState.BeforeWait = cancellationSource.Cancel;

        Assert.Throws<OperationCanceledException>(
            () => test.Accessor.RunOneIteration(cancellationSource.Token, ref lastTime, ref accumulator));

        Assert.Equal(positionBefore, test.Shared.Queue.ProducerPosition);
        Assert.Equal(
            [GetExpectedPressureDelay(positionBefore, test.Shared.Queue.ConsumerPosition)],
            test.WaiterState.WaitCalls);
        Assert.Equal(TestPipelineConfiguration.TickDurationNanoseconds, accumulator);
    }

    // ── RunOneIteration — hard ceiling ───────────────────────────────────────

    [Fact]
    public void RunOneIteration_BlocksAtHardCeiling_UntilConsumptionCatchesUp()
    {
        var test = ProductionLoopTestContext.Create();

        test.Accessor.Bootstrap();
        for (var i = 0; i < HardCeilingTicks; i++)
            test.Accessor.Tick();

        test.WaiterState.WaitCalls.Clear();

        var waitCount = 0;
        test.WaiterState.BeforeWait = () =>
        {
            waitCount++;
            if (waitCount == 1)
                test.Shared.Queue.ConsumerAdvance(2);
        };

        long lastTime = 0;
        var accumulator = TestPipelineConfiguration.TickDurationNanoseconds;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(HardCeilingTicks + 2, test.Shared.Queue.ProducerPosition);

        var hardCeilingWait = GetHardCeilingWait();
        Assert.Equal(hardCeilingWait, test.WaiterState.WaitCalls[0]);
        Assert.True(test.WaiterState.WaitCalls.Count >= 3,
            "Expected hard ceiling wait, soft pressure delay, and idle yield");
        Assert.Equal(GetIdleYieldWait(), test.WaiterState.WaitCalls[^1]);
    }

    [Fact]
    public void RunOneIteration_ThrowsWhenCancelledDuringHardCeilingWait()
    {
        var test = ProductionLoopTestContext.Create();

        test.Accessor.Bootstrap();
        for (var i = 0; i < HardCeilingTicks; i++)
            test.Accessor.Tick();

        var positionBefore = test.Shared.Queue.ProducerPosition;
        test.WaiterState.WaitCalls.Clear();

        using var cancellationSource = new CancellationTokenSource();
        test.WaiterState.BeforeWait = cancellationSource.Cancel;

        long lastTime = 0;
        var accumulator = TestPipelineConfiguration.TickDurationNanoseconds;

        Assert.Throws<OperationCanceledException>(
            () => test.Accessor.RunOneIteration(cancellationSource.Token, ref lastTime, ref accumulator));

        Assert.Equal(positionBefore, test.Shared.Queue.ProducerPosition);
        Assert.Equal(
            [GetHardCeilingWait()],
            test.WaiterState.WaitCalls);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static TimeSpan GetIdleYieldWait()
        => TimeSpan.FromTicks(TestPipelineConfiguration.IdleYieldNanoseconds / 100);

    private static TimeSpan GetHardCeilingWait()
        => TimeSpan.FromTicks(TestPipelineConfiguration.PressureMaxDelayNanoseconds / 100);

    private static TimeSpan GetExpectedPressureDelay(long producerPosition, long consumerPosition)
        => TimeSpan.FromTicks(
            SimulationPressure.ComputeDelay(
                gapNanoseconds: (producerPosition - consumerPosition) * TestPipelineConfiguration.TickDurationNanoseconds,
                lowWaterMarkNanoseconds: TestPipelineConfiguration.PressureLowWaterMarkNanoseconds,
                bucketWidthNanoseconds: TestPipelineConfiguration.TickDurationNanoseconds,
                bucketCount: TestPipelineConfiguration.PressureBucketCount,
                baseNanoseconds: TestPipelineConfiguration.PressureBaseDelayNanoseconds,
                maxNanoseconds: TestPipelineConfiguration.PressureMaxDelayNanoseconds) / 100);

    private static class ProductionLoopTestContext
    {
        public static ProductionLoopTestContext<TestFakeClock, TestRecordingWaiter> Create()
        {
            var waiterState = new TestWaiterState();
            return Create(
                clock: new TestFakeClock(),
                waiter: new TestRecordingWaiter(waiterState),
                waiterState: waiterState);
        }

        public static ProductionLoopTestContext<TClock, TWaiter> Create<TClock, TWaiter>(
            TClock clock,
            TWaiter waiter,
            TestWaiterState waiterState,
            int poolSize = 8)
            where TClock : struct, IClock
            where TWaiter : struct, IWaiter
            => ProductionLoopTestContext<TClock, TWaiter>.Create(clock, waiter, waiterState, poolSize);
    }

    private sealed class ProductionLoopTestContext<TClock, TWaiter>
        where TClock : struct, IClock
        where TWaiter : struct, IWaiter
    {
        public static ProductionLoopTestContext<TClock, TWaiter> Create(
            TClock clock,
            TWaiter waiter,
            TestWaiterState waiterState,
            int poolSize = 8)
        {
            var queue = new ProducerConsumerQueue<PipelineEntry<TestPayload>>();
            var payloadPool = new ObjectPool<TestPayload, TestPayload.Allocator>(poolSize);
            var shared = new SharedPipelineState<TestPayload>(queue);
            var producer = new TestWorkerProducer(payloadPool, 1);
            var loop = new ProductionLoop<TestPayload, TestWorkerProducer, TClock, TWaiter>(
                shared, producer, clock, waiter, TestPipelineConfiguration.Default);
            return new ProductionLoopTestContext<TClock, TWaiter>(shared, waiterState, loop);
        }

        private ProductionLoopTestContext(
            SharedPipelineState<TestPayload> shared,
            TestWaiterState waiterState,
            ProductionLoop<TestPayload, TestWorkerProducer, TClock, TWaiter> loop)
        {
            this.Shared = shared;
            this.WaiterState = waiterState;
            this.Loop = loop;
            this.Accessor = loop.GetTestAccessor();
        }

        public PinnedVersions PinnedVersions => this.Shared.PinnedVersions;

        public SharedPipelineState<TestPayload> Shared { get; }

        public TestWaiterState WaiterState { get; }

        public ProductionLoop<TestPayload, TestWorkerProducer, TClock, TWaiter> Loop { get; }

        public ProductionLoop<TestPayload, TestWorkerProducer, TClock, TWaiter>.TestAccessor Accessor { get; }
    }

    private sealed class TestPinOwner : IPinOwner;
}
