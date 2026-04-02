using Fabrica.Core.Collections;
using Fabrica.Core.Memory;
using Fabrica.Core.Threading;
using Fabrica.Pipeline.Tests.Helpers;
using Xunit;

namespace Fabrica.Pipeline.Tests.Pipeline;

using SimulationPressure = ProductionLoop<TestPayload, TestWorkerProducer, TestFakeClock, TestRecordingWaiter>.SimulationPressure;

public sealed class ProductionLoopAdditionalTests
{
    private static int LowWaterMarkTicks =>
        (int)(TestPipelineConfiguration.PressureLowWaterMarkNanoseconds / TestPipelineConfiguration.TickDurationNanoseconds);

    [Fact]
    public void Cleanup_QueuesMultiplePinnedPayloads_AndDrainsThemAfterUnpin()
    {
        var test = ProductionLoopTestContext.Create();
        var tick0Owner = new TestPinOwner();
        var tick1Owner = new TestPinOwner();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();
        test.Accessor.Tick();

        test.PinnedVersions.Pin(0, tick0Owner);
        test.PinnedVersions.Pin(1, tick1Owner);
        test.Shared.Queue.ConsumerAdvance(3);

        test.Accessor.Cleanup();

        Assert.Equal(2, test.Accessor.PinnedPayloadCount);

        test.PinnedVersions.Unpin(0, tick0Owner);
        test.PinnedVersions.Unpin(1, tick1Owner);

        test.Accessor.Cleanup();

        Assert.Equal(0, test.Accessor.PinnedPayloadCount);
    }

    [Fact]
    public void Cleanup_KeepsStillPinnedPayloads_AndDrainsOnlyReleasedOnes()
    {
        var test = ProductionLoopTestContext.Create();
        var tick0Owner = new TestPinOwner();
        var tick1Owner = new TestPinOwner();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();
        test.Accessor.Tick();

        test.PinnedVersions.Pin(0, tick0Owner);
        test.PinnedVersions.Pin(1, tick1Owner);
        test.Shared.Queue.ConsumerAdvance(3);

        test.Accessor.Cleanup();

        Assert.Equal(2, test.Accessor.PinnedPayloadCount);

        test.PinnedVersions.Unpin(0, tick0Owner);
        test.Accessor.Cleanup();

        Assert.Equal(1, test.Accessor.PinnedPayloadCount);

        test.PinnedVersions.Unpin(1, tick1Owner);
        test.Accessor.Cleanup();

        Assert.Equal(0, test.Accessor.PinnedPayloadCount);
    }

    [Fact]
    public void RunOneIteration_WithNoPressure_OnlyPerformsTheFinalIdleWait()
    {
        var test = ProductionLoopTestContext.Create();

        test.Accessor.Bootstrap();

        long lastTime = 0;
        var accumulator = TestPipelineConfiguration.TickDurationNanoseconds;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(2, test.Shared.Queue.ProducerPosition);
        Assert.Equal(
            [GetIdleYieldWait()],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_WithFirstPressureBucket_WaitsBaseDelayThenIdleWait()
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
    public void RunOneIteration_UsesPressureDelayBeforeTick_ThenIdleWait()
    {
        var test = ProductionLoopTestContext.Create();

        test.Accessor.Bootstrap();
        for (var i = 0; i < LowWaterMarkTicks + 2; i++)
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
    public void RunOneIteration_UsesPressureDelayForEachDueTick_BeforeFinalIdleWait()
    {
        var test = ProductionLoopTestContext.Create();

        test.Accessor.Bootstrap();
        for (var i = 0; i < LowWaterMarkTicks + 1; i++)
            test.Accessor.Tick();

        test.WaiterState.WaitCalls.Clear();

        long lastTime = 0;
        var accumulator = TestPipelineConfiguration.TickDurationNanoseconds * 2;
        var positionsBeforeRun = test.Shared.Queue.ProducerPosition;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(positionsBeforeRun + 2, test.Shared.Queue.ProducerPosition);
        Assert.Equal(
            [
                GetExpectedPressureDelay(positionsBeforeRun, test.Shared.Queue.ConsumerPosition),
                GetExpectedPressureDelay(positionsBeforeRun + 1, test.Shared.Queue.ConsumerPosition),
                GetIdleYieldWait(),
            ],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_ThrowsWhenCancelledDuringNonTrivialPressureDelay()
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
        Assert.Equal(TestPipelineConfiguration.TickDurationNanoseconds, accumulator);
        Assert.Equal(
            [GetExpectedPressureDelay(positionBefore, test.Shared.Queue.ConsumerPosition)],
            test.WaiterState.WaitCalls);
    }

    private static TimeSpan GetIdleYieldWait()
        => TimeSpan.FromTicks(TestPipelineConfiguration.IdleYieldNanoseconds / 100);

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
