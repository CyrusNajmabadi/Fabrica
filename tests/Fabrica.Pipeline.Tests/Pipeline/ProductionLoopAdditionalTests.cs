using Fabrica.Core.Memory;
using Fabrica.Core.Threading;
using Fabrica.Pipeline.Tests.Helpers;
using Xunit;

namespace Fabrica.Pipeline.Tests.Pipeline;

using ChainNode = BaseProductionLoop<TestPayload>.ChainNode;
using ChainNodeAllocator = BaseProductionLoop<TestPayload>.ChainNode.Allocator;
using SimulationPressure = ProductionLoop<TestPayload, TestWorkerProducer, TestFakeClock, TestRecordingWaiter>.SimulationPressure;

public sealed class ProductionLoopAdditionalTests
{
    private static int LowWaterMarkTicks =>
        (int)(TestPipelineConfiguration.PressureLowWaterMarkNanoseconds / TestPipelineConfiguration.TickDurationNanoseconds);

    [Fact]
    public void Cleanup_QueuesMultiplePinnedStaleSnapshots_AndDrainsThemAfterUnpin()
    {
        var test = ProductionLoopTestContext.Create();
        var tick0Owner = new TestPinOwner();
        var tick1Owner = new TestPinOwner();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();
        test.Accessor.Tick();

        var tick0 = test.Accessor.OldestNode!;
        var tick1 = test.Accessor.GetNext(tick0)!;
        var tick2 = test.Accessor.GetNext(tick1)!;
        var tick3 = test.Accessor.CurrentNode!;

        test.PinnedVersions.Pin(tick0.SequenceNumber, tick0Owner);
        test.PinnedVersions.Pin(tick1.SequenceNumber, tick1Owner);
        test.Shared.ConsumptionEpoch = 3;

        test.Accessor.CleanupStaleNodes();

        Assert.Same(tick3, test.Accessor.OldestNode);
        Assert.Same(tick3, test.Accessor.CurrentNode);
        Assert.Equal(2, test.Accessor.PinnedQueueCount);
        Assert.Null(test.Accessor.GetNext(tick0));
        Assert.Null(test.Accessor.GetNext(tick1));
        Assert.True(tick2.IsUnreferenced);

        test.PinnedVersions.Unpin(tick0.SequenceNumber, tick0Owner);
        test.PinnedVersions.Unpin(tick1.SequenceNumber, tick1Owner);

        test.Accessor.CleanupStaleNodes();

        Assert.Equal(0, test.Accessor.PinnedQueueCount);
    }

    [Fact]
    public void Cleanup_KeepsStillPinnedQueuedSnapshots_AndDrainsOnlyReleasedOnes()
    {
        var test = ProductionLoopTestContext.Create();
        var tick0Owner = new TestPinOwner();
        var tick1Owner = new TestPinOwner();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();
        test.Accessor.Tick();

        var tick0 = test.Accessor.OldestNode!;
        var tick1 = test.Accessor.GetNext(tick0)!;
        var tick3 = test.Accessor.CurrentNode!;

        test.PinnedVersions.Pin(tick0.SequenceNumber, tick0Owner);
        test.PinnedVersions.Pin(tick1.SequenceNumber, tick1Owner);
        test.Shared.ConsumptionEpoch = 3;

        test.Accessor.CleanupStaleNodes();

        Assert.Equal(2, test.Accessor.PinnedQueueCount);

        test.PinnedVersions.Unpin(tick0.SequenceNumber, tick0Owner);
        test.Accessor.CleanupStaleNodes();

        Assert.Equal(1, test.Accessor.PinnedQueueCount);
        Assert.True(tick0.IsUnreferenced);
        Assert.False(tick1.IsUnreferenced);
        Assert.Same(tick3, test.Accessor.OldestNode);

        test.PinnedVersions.Unpin(tick1.SequenceNumber, tick1Owner);
        test.Accessor.CleanupStaleNodes();

        Assert.Equal(0, test.Accessor.PinnedQueueCount);
        Assert.True(tick1.IsUnreferenced);
    }

    [Fact]
    public void Cleanup_ThrowsWhenTheSamePinnedSnapshotWouldBeQueuedTwice()
    {
        var test = ProductionLoopTestContext.Create();
        var pinOwner = new TestPinOwner();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();
        test.Accessor.Tick();

        var tick0 = test.Accessor.OldestNode!;

        test.PinnedVersions.Pin(tick0.SequenceNumber, pinOwner);
        test.Shared.ConsumptionEpoch = 2;
        test.Accessor.CleanupStaleNodes();

        Assert.Equal(1, test.Accessor.PinnedQueueCount);

        test.Accessor.SetOldestNodeForTesting(tick0);

        var exception = Assert.Throws<InvalidOperationException>(
            test.Accessor.CleanupStaleNodes);

        Assert.Contains("more than once", exception.Message);
        Assert.Equal(3, test.Accessor.CurrentNode!.SequenceNumber);
    }

    [Fact]
    public void RunOneIteration_WithNoPressure_OnlyPerformsTheFinalIdleWait()
    {
        var test = ProductionLoopTestContext.Create();

        test.Accessor.Bootstrap();

        long lastTime = 0;
        var accumulator = TestPipelineConfiguration.TickDurationNanoseconds;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(1, test.Accessor.CurrentSequence);
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

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        var tickBefore = LowWaterMarkTicks + 1;
        Assert.Equal(tickBefore + 1, test.Accessor.CurrentSequence);
        Assert.Equal(
            [
                GetExpectedPressureDelay(outstandingTicks: tickBefore),
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

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        var tickBefore = LowWaterMarkTicks + 2;
        Assert.Equal(tickBefore + 1, test.Accessor.CurrentSequence);
        Assert.Equal(
            [
                GetExpectedPressureDelay(outstandingTicks: tickBefore),
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

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        var tickBefore = LowWaterMarkTicks + 1;
        Assert.Equal(tickBefore + 2, test.Accessor.CurrentSequence);
        Assert.Equal(
            [
                GetExpectedPressureDelay(outstandingTicks: tickBefore),
                GetExpectedPressureDelay(outstandingTicks: tickBefore + 1),
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

        var tickBefore = test.Accessor.CurrentSequence;
        test.WaiterState.WaitCalls.Clear();

        long lastTime = 0;
        var accumulator = TestPipelineConfiguration.TickDurationNanoseconds;

        using var cancellationSource = new CancellationTokenSource();
        test.WaiterState.BeforeWait = cancellationSource.Cancel;

        Assert.Throws<OperationCanceledException>(
            () => test.Accessor.RunOneIteration(cancellationSource.Token, ref lastTime, ref accumulator));

        Assert.Equal(tickBefore, test.Accessor.CurrentSequence);
        Assert.Equal(TestPipelineConfiguration.TickDurationNanoseconds, accumulator);
        Assert.Equal(
            [GetExpectedPressureDelay(outstandingTicks: tickBefore)],
            test.WaiterState.WaitCalls);
    }

    private static TimeSpan GetIdleYieldWait() =>
        TimeSpan.FromTicks(TestPipelineConfiguration.IdleYieldNanoseconds / 100);

    private static TimeSpan GetExpectedPressureDelay(int outstandingTicks) =>
        TimeSpan.FromTicks(
            SimulationPressure.ComputeDelay(
                gapNanoseconds: outstandingTicks * TestPipelineConfiguration.TickDurationNanoseconds,
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
            where TWaiter : struct, IWaiter =>
            ProductionLoopTestContext<TClock, TWaiter>.Create(clock, waiter, waiterState, poolSize);
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
            var nodePool = new ObjectPool<ChainNode, ChainNodeAllocator>(poolSize);
            var payloadPool = new ObjectPool<TestPayload, TestPayload.Allocator>(poolSize);
            var shared = new SharedPipelineState<TestPayload>();
            var producer = new TestWorkerProducer(payloadPool, 1);
            var loop = new ProductionLoop<TestPayload, TestWorkerProducer, TClock, TWaiter>(
                nodePool, shared, producer, clock, waiter, TestPipelineConfiguration.Default);
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
