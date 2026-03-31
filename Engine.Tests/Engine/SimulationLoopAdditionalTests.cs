using Engine;
using Engine.Memory;
using Engine.Pipeline;
using Engine.Simulation;
using Engine.Tests.Helpers;
using Engine.Threading;
using Engine.World;
using Xunit;

namespace Engine.Tests;

public sealed class SimulationLoopAdditionalTests
{
    private static int LowWaterMarkTicks =>
        (int)(SimulationConstants.PressureLowWaterMarkNanoseconds / SimulationConstants.TickDurationNanoseconds);

    [Fact]
    public void Cleanup_QueuesMultiplePinnedStaleSnapshots_AndDrainsThemAfterUnpin()
    {
        var test = SimulationLoopTestContext.Create();
        var tick0Owner = new TestPinOwner();
        var tick1Owner = new TestPinOwner();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();
        test.Accessor.Tick();

        var tick0 = Assert.IsType<ChainNode<WorldImage>>(test.Accessor.OldestNode);
        var tick1 = Assert.IsType<ChainNode<WorldImage>>(tick0.NextInChain);
        var tick2 = Assert.IsType<ChainNode<WorldImage>>(tick1.NextInChain);
        var tick3 = Assert.IsType<ChainNode<WorldImage>>(test.Accessor.CurrentNode);

        test.PinnedVersions.Pin(tick0.SequenceNumber, tick0Owner);
        test.PinnedVersions.Pin(tick1.SequenceNumber, tick1Owner);
        test.Shared.ConsumptionEpoch = 3;

        test.Accessor.CleanupStaleNodes();

        Assert.Same(tick3, test.Accessor.OldestNode);
        Assert.Same(tick3, test.Accessor.CurrentNode);
        Assert.Equal(2, test.Accessor.PinnedQueueCount);
        Assert.Null(tick0.NextInChain);
        Assert.Null(tick1.NextInChain);
        Assert.True(tick2.IsUnreferenced);

        test.PinnedVersions.Unpin(tick0.SequenceNumber, tick0Owner);
        test.PinnedVersions.Unpin(tick1.SequenceNumber, tick1Owner);

        test.Accessor.CleanupStaleNodes();

        Assert.Equal(0, test.Accessor.PinnedQueueCount);
    }

    [Fact]
    public void Cleanup_KeepsStillPinnedQueuedSnapshots_AndDrainsOnlyReleasedOnes()
    {
        var test = SimulationLoopTestContext.Create();
        var tick0Owner = new TestPinOwner();
        var tick1Owner = new TestPinOwner();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();
        test.Accessor.Tick();

        var tick0 = Assert.IsType<ChainNode<WorldImage>>(test.Accessor.OldestNode);
        var tick1 = Assert.IsType<ChainNode<WorldImage>>(tick0.NextInChain);
        var tick3 = Assert.IsType<ChainNode<WorldImage>>(test.Accessor.CurrentNode);

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
        var test = SimulationLoopTestContext.Create();
        var pinOwner = new TestPinOwner();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();
        test.Accessor.Tick();

        var tick0 = Assert.IsType<ChainNode<WorldImage>>(test.Accessor.OldestNode);

        test.PinnedVersions.Pin(tick0.SequenceNumber, pinOwner);
        test.Shared.ConsumptionEpoch = 2;
        test.Accessor.CleanupStaleNodes();

        Assert.Equal(1, test.Accessor.PinnedQueueCount);

        test.Accessor.SetOldestNodeForTesting(tick0);

        var exception = Assert.Throws<InvalidOperationException>(
            test.Accessor.CleanupStaleNodes);

        Assert.Contains("more than once", exception.Message);
        Assert.Equal(3, Assert.IsType<ChainNode<WorldImage>>(test.Accessor.CurrentNode).SequenceNumber);
    }

    [Fact]
    public void RunOneIteration_WithNoPressure_OnlyPerformsTheFinalIdleWait()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();

        long lastTime = 0;
        var accumulator = SimulationConstants.TickDurationNanoseconds;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(1, test.Accessor.CurrentSequence);
        Assert.Equal(
            [GetIdleYieldWait()],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_WithFirstPressureBucket_WaitsBaseDelayThenIdleWait()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        for (var i = 0; i < LowWaterMarkTicks + 1; i++)
            test.Accessor.Tick();

        test.WaiterState.WaitCalls.Clear();

        long lastTime = 0;
        var accumulator = SimulationConstants.TickDurationNanoseconds;

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
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        for (var i = 0; i < LowWaterMarkTicks + 2; i++)
            test.Accessor.Tick();

        test.WaiterState.WaitCalls.Clear();

        long lastTime = 0;
        var accumulator = SimulationConstants.TickDurationNanoseconds;

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
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        for (var i = 0; i < LowWaterMarkTicks + 1; i++)
            test.Accessor.Tick();

        test.WaiterState.WaitCalls.Clear();

        long lastTime = 0;
        var accumulator = SimulationConstants.TickDurationNanoseconds * 2;

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
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        for (var i = 0; i < LowWaterMarkTicks + 1; i++)
            test.Accessor.Tick();

        var tickBefore = test.Accessor.CurrentSequence;
        test.WaiterState.WaitCalls.Clear();

        long lastTime = 0;
        var accumulator = SimulationConstants.TickDurationNanoseconds;

        using var cancellationSource = new CancellationTokenSource();
        test.WaiterState.BeforeWait = cancellationSource.Cancel;

        Assert.Throws<OperationCanceledException>(
            () => test.Accessor.RunOneIteration(cancellationSource.Token, ref lastTime, ref accumulator));

        Assert.Equal(tickBefore, test.Accessor.CurrentSequence);
        Assert.Equal(SimulationConstants.TickDurationNanoseconds, accumulator);
        Assert.Equal(
            [GetExpectedPressureDelay(outstandingTicks: tickBefore)],
            test.WaiterState.WaitCalls);
    }

    private static TimeSpan GetIdleYieldWait() =>
        TimeSpan.FromTicks(SimulationConstants.IdleYieldNanoseconds / 100);

    private static TimeSpan GetExpectedPressureDelay(int outstandingTicks) =>
        TimeSpan.FromTicks(
            SimulationPressure.ComputeDelay(
                gapNanoseconds: (long)outstandingTicks * SimulationConstants.TickDurationNanoseconds,
                lowWaterMarkNanoseconds: SimulationConstants.PressureLowWaterMarkNanoseconds,
                bucketWidthNanoseconds: SimulationConstants.TickDurationNanoseconds,
                bucketCount: SimulationConstants.PressureBucketCount,
                baseNanoseconds: SimulationConstants.PressureBaseDelayNanoseconds,
                maxNanoseconds: SimulationConstants.PressureMaxDelayNanoseconds) / 100);

    private static class SimulationLoopTestContext
    {
        public static SimulationLoopTestContext<TestFakeClock, TestRecordingWaiter> Create()
        {
            var waiterState = new TestWaiterState();
            return Create(
                clock: new TestFakeClock(),
                waiter: new TestRecordingWaiter(waiterState),
                waiterState: waiterState);
        }

        public static SimulationLoopTestContext<TClock, TWaiter> Create<TClock, TWaiter>(
            TClock clock,
            TWaiter waiter,
            TestWaiterState waiterState,
            int poolSize = 8)
            where TClock : struct, IClock
            where TWaiter : struct, IWaiter
        {
            var nodePool = new ObjectPool<ChainNode<WorldImage>, ChainNodeAllocator<WorldImage>>(poolSize);
            var imagePool = new ObjectPool<WorldImage, WorldImageAllocator>(poolSize);
            var pinnedVersions = new PinnedVersions();
            var shared = new SharedState<WorldImage>();
            var producer = new SimulationProducer(imagePool, new SimulationCoordinator(1));
            var loop = new ProductionLoop<WorldImage, SimulationProducer, TClock, TWaiter>(
                nodePool, pinnedVersions, shared, producer, clock, waiter);
            return new SimulationLoopTestContext<TClock, TWaiter>(pinnedVersions, shared, waiterState, loop);
        }
    }

    private sealed class SimulationLoopTestContext<TClock, TWaiter>
        where TClock : struct, IClock
        where TWaiter : struct, IWaiter
    {
        internal SimulationLoopTestContext(
            PinnedVersions pinnedVersions,
            SharedState<WorldImage> shared,
            TestWaiterState waiterState,
            ProductionLoop<WorldImage, SimulationProducer, TClock, TWaiter> loop)
        {
            this.PinnedVersions = pinnedVersions;
            this.Shared = shared;
            this.WaiterState = waiterState;
            this.Accessor = loop.GetTestAccessor();
        }

        public PinnedVersions PinnedVersions { get; }

        public SharedState<WorldImage> Shared { get; }

        public TestWaiterState WaiterState { get; }

        public ProductionLoop<WorldImage, SimulationProducer, TClock, TWaiter>.TestAccessor Accessor { get; }
    }

    private sealed class TestPinOwner : IPinOwner;
}
