using Engine;
using Engine.Memory;
using Engine.Pipeline;
using Engine.Simulation;
using Engine.Tests.Helpers;
using Engine.Threading;
using Engine.World;
using Xunit;

namespace Engine.Tests;

using ChainNode = BaseProductionLoop<WorldImage>.ChainNode;
using ChainNodeAllocator = BaseProductionLoop<WorldImage>.ChainNode.Allocator;
using SimulationPressure = ProductionLoop<WorldImage, SimulationProducer, TestFakeClock, TestRecordingWaiter>.SimulationPressure;

public sealed class SimulationLoopTickTests
{
    private static int LowWaterMarkTicks =>
        (int)(SimulationConstants.PressureLowWaterMarkNanoseconds / SimulationConstants.TickDurationNanoseconds);

    private static int HardCeilingTicks =>
        (int)(SimulationConstants.PressureHardCeilingNanoseconds / SimulationConstants.TickDurationNanoseconds);

    [Fact]
    public void Bootstrap_PublishesTickZeroSnapshot_AsCurrentAndOldest()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();

        var node = test.Accessor.CurrentNode!;

        Assert.Equal(0, test.Accessor.CurrentSequence);
        Assert.Same(node, test.Accessor.OldestNode);
        Assert.Same(node, test.Shared.LatestNode);
        Assert.Equal(0, node.SequenceNumber);
        Assert.Null(test.Accessor.GetNext(node));
    }

    [Fact]
    public void Tick_PublishesNextSnapshot()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        var initial = test.Accessor.CurrentNode!;

        test.Accessor.Tick();

        Assert.Equal(1, test.Accessor.CurrentSequence);
        Assert.NotSame(initial, test.Accessor.CurrentNode);
        Assert.Same(test.Accessor.CurrentNode, test.Shared.LatestNode);
        Assert.Equal(1, test.Accessor.CurrentNode!.SequenceNumber);
        Assert.Same(test.Accessor.CurrentNode, test.Accessor.GetNext(initial));
    }

    [Fact]
    public void Cleanup_FreesUnpinnedSnapshotsOlderThanConsumptionEpoch()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();

        var secondNode = test.Accessor.CurrentNode;
        var firstNode = test.Accessor.OldestNode;

        test.Shared.ConsumptionEpoch = 2;

        test.Accessor.CleanupStaleNodes();

        Assert.NotSame(firstNode, test.Accessor.OldestNode);
        Assert.Same(secondNode, test.Accessor.CurrentNode);
        Assert.Equal(0, test.Accessor.PinnedQueueCount);
    }

    [Fact]
    public void Cleanup_ResetsFreedImageBeforeItCanBeReused()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();

        var firstNode = test.Accessor.OldestNode!;

        test.Shared.ConsumptionEpoch = 2;

        test.Accessor.CleanupStaleNodes();

        Assert.True(firstNode.IsUnreferenced);
    }

    [Fact]
    public void Cleanup_DoesNotFreeTheCurrentSnapshot_EvenWhenConsumptionEpochIsPastIt()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        var currentNode = test.Accessor.CurrentNode;

        test.Shared.ConsumptionEpoch = 100;

        test.Accessor.CleanupStaleNodes();

        Assert.Same(currentNode, test.Accessor.CurrentNode);
        Assert.Same(currentNode, test.Accessor.OldestNode);
        Assert.Same(currentNode, test.Shared.LatestNode);
        Assert.Equal(0, test.Accessor.PinnedQueueCount);
    }

    [Fact]
    public void Cleanup_DoesNotFreeSnapshot_AtConsumptionEpochBoundary()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();

        test.Shared.ConsumptionEpoch = 1;

        test.Accessor.CleanupStaleNodes();

        Assert.Equal(1, test.Accessor.OldestNode!.SequenceNumber);
        Assert.Equal(2, test.Accessor.CurrentNode!.SequenceNumber);
        Assert.Equal(0, test.Accessor.PinnedQueueCount);
    }

    [Fact]
    public void Cleanup_DetachesPinnedSnapshotsUntilTheyAreUnpinned()
    {
        var test = SimulationLoopTestContext.Create();
        var pinOwner = new TestPinOwner();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();

        var firstNode = test.Accessor.OldestNode!;
        var latestNode = test.Accessor.CurrentNode!;

        test.PinnedVersions.Pin(firstNode.SequenceNumber, pinOwner);
        test.Shared.ConsumptionEpoch = 2;

        test.Accessor.CleanupStaleNodes();

        Assert.NotSame(firstNode, test.Accessor.OldestNode);
        Assert.Same(latestNode, test.Accessor.CurrentNode);
        Assert.Equal(1, test.Accessor.PinnedQueueCount);

        test.PinnedVersions.Unpin(firstNode.SequenceNumber, pinOwner);

        test.Accessor.CleanupStaleNodes();

        Assert.Equal(0, test.Accessor.PinnedQueueCount);
    }

    [Fact]
    public void Cleanup_PinnedMiddleSnapshot_DoesNotBlockLaterSnapshotsFromBeingFreed()
    {
        var test = SimulationLoopTestContext.Create();
        var pinOwner = new TestPinOwner();

        test.Accessor.Bootstrap();
        test.Accessor.Tick(); // tick 1
        test.Accessor.Tick(); // tick 2
        test.Accessor.Tick(); // tick 3

        var tick0 = test.Accessor.OldestNode!;
        var tick1 = test.Accessor.GetNext(tick0)!;
        var tick2 = test.Accessor.GetNext(tick1)!;
        var tick3 = test.Accessor.CurrentNode!;

        test.PinnedVersions.Pin(tick1.SequenceNumber, pinOwner);
        test.Shared.ConsumptionEpoch = 3;

        test.Accessor.CleanupStaleNodes();

        Assert.Same(tick3, test.Accessor.CurrentNode);
        Assert.Same(tick3, test.Accessor.OldestNode);
        Assert.Equal(1, test.Accessor.PinnedQueueCount);
        Assert.Null(test.Accessor.GetNext(tick1));
        Assert.Equal(1, tick1.SequenceNumber);
        Assert.Equal(3, tick3.SequenceNumber);

        test.PinnedVersions.Unpin(tick1.SequenceNumber, pinOwner);
        test.Accessor.CleanupStaleNodes();

        Assert.Equal(0, test.Accessor.PinnedQueueCount);
    }

    // ── RunOneIteration — basic ──────────────────────────────────────────────

    [Fact]
    public void RunOneIteration_DoesNotTick_WhenAccumulatorIsBelowThreshold()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();

        long lastTime = 0;
        var accumulator = SimulationConstants.TickDurationNanoseconds - 1;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(0, test.Accessor.CurrentSequence);
        Assert.Equal(SimulationConstants.TickDurationNanoseconds - 1, accumulator);
        Assert.Equal(
            [GetIdleYieldWait()],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_TicksOnce_WhenAccumulatorReachesThreshold()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();

        long lastTime = 0;
        var accumulator = SimulationConstants.TickDurationNanoseconds;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(1, test.Accessor.CurrentSequence);
        Assert.Equal(0, accumulator);
        Assert.Equal(
            [GetIdleYieldWait()],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_ThrowsWhenCancelledDuringIdleWait()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();

        long lastTime = 0;
        long accumulator = 0;

        using var cancellationSource = new CancellationTokenSource();
        test.WaiterState.BeforeWait = cancellationSource.Cancel;

        Assert.Throws<OperationCanceledException>(
            () => test.Accessor.RunOneIteration(cancellationSource.Token, ref lastTime, ref accumulator));

        Assert.Equal(0, test.Accessor.CurrentSequence);
        Assert.Equal(
            [GetIdleYieldWait()],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_ProcessesMultipleTicks_AndPreservesLeftoverAccumulator()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();

        long lastTime = 0;
        var accumulator = (SimulationConstants.TickDurationNanoseconds * 2) + 123;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(2, test.Accessor.CurrentSequence);
        Assert.Equal(123, accumulator);
        Assert.Equal(
            [GetIdleYieldWait()],
            test.WaiterState.WaitCalls);
    }

    // ── RunOneIteration — soft pressure ──────────────────────────────────────

    [Fact]
    public void RunOneIteration_AppliesPressureDelay_WhenTickEpochGapExceedsLowWaterMark()
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
    public void RunOneIteration_ThrowsWhenCancelledDuringPressureDelay()
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
        Assert.Equal(
            [GetExpectedPressureDelay(outstandingTicks: tickBefore)],
            test.WaiterState.WaitCalls);
        Assert.Equal(SimulationConstants.TickDurationNanoseconds, accumulator);
    }

    // ── RunOneIteration — hard ceiling ───────────────────────────────────────

    [Fact]
    public void RunOneIteration_BlocksAtHardCeiling_UntilConsumptionCatchesUp()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        for (var i = 0; i < HardCeilingTicks; i++)
            test.Accessor.Tick();

        test.WaiterState.WaitCalls.Clear();

        var waitCount = 0;
        test.WaiterState.BeforeWait = () =>
        {
            waitCount++;
            if (waitCount == 1)
                test.Shared.ConsumptionEpoch = 1;
        };

        long lastTime = 0;
        var accumulator = SimulationConstants.TickDurationNanoseconds;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(HardCeilingTicks + 1, test.Accessor.CurrentSequence);

        var hardCeilingWait = GetHardCeilingWait();
        Assert.Equal(hardCeilingWait, test.WaiterState.WaitCalls[0]);
        Assert.True(test.WaiterState.WaitCalls.Count >= 3,
            "Expected hard ceiling wait, soft pressure delay, and idle yield");
        Assert.Equal(GetIdleYieldWait(), test.WaiterState.WaitCalls[^1]);
    }

    [Fact]
    public void RunOneIteration_ThrowsWhenCancelledDuringHardCeilingWait()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        for (var i = 0; i < HardCeilingTicks; i++)
            test.Accessor.Tick();

        var tickBefore = test.Accessor.CurrentSequence;
        test.WaiterState.WaitCalls.Clear();

        using var cancellationSource = new CancellationTokenSource();
        test.WaiterState.BeforeWait = cancellationSource.Cancel;

        long lastTime = 0;
        var accumulator = SimulationConstants.TickDurationNanoseconds;

        Assert.Throws<OperationCanceledException>(
            () => test.Accessor.RunOneIteration(cancellationSource.Token, ref lastTime, ref accumulator));

        Assert.Equal(tickBefore, test.Accessor.CurrentSequence);
        Assert.Equal(
            [GetHardCeilingWait()],
            test.WaiterState.WaitCalls);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static TimeSpan GetIdleYieldWait() =>
        TimeSpan.FromTicks(SimulationConstants.IdleYieldNanoseconds / 100);

    private static TimeSpan GetHardCeilingWait() =>
        TimeSpan.FromTicks(SimulationConstants.PressureMaxDelayNanoseconds / 100);

    private static TimeSpan GetExpectedPressureDelay(int outstandingTicks) =>
        TimeSpan.FromTicks(
            SimulationPressure.ComputeDelay(
                gapNanoseconds: outstandingTicks * SimulationConstants.TickDurationNanoseconds,
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
            where TWaiter : struct, IWaiter =>
            SimulationLoopTestContext<TClock, TWaiter>.Create(clock, waiter, waiterState, poolSize);
    }

    private sealed class SimulationLoopTestContext<TClock, TWaiter>
        where TClock : struct, IClock
        where TWaiter : struct, IWaiter
    {
        public static SimulationLoopTestContext<TClock, TWaiter> Create(
            TClock clock,
            TWaiter waiter,
            TestWaiterState waiterState,
            int poolSize = 8)
        {
            var nodePool = new ObjectPool<ChainNode, ChainNodeAllocator>(poolSize);
            var imagePool = new ObjectPool<WorldImage, WorldImage.Allocator>(poolSize);
            var shared = new SharedState<WorldImage>();
            var producer = new SimulationProducer(imagePool, new SimulationCoordinator(1));
            var loop = new ProductionLoop<WorldImage, SimulationProducer, TClock, TWaiter>(
                nodePool, shared, producer, clock, waiter);
            return new SimulationLoopTestContext<TClock, TWaiter>(shared, waiterState, loop);
        }

        private SimulationLoopTestContext(
            SharedState<WorldImage> shared,
            TestWaiterState waiterState,
            ProductionLoop<WorldImage, SimulationProducer, TClock, TWaiter> loop)
        {
            this.Shared = shared;
            this.WaiterState = waiterState;
            this.Loop = loop;
            this.Accessor = loop.GetTestAccessor();
        }

        public PinnedVersions PinnedVersions => this.Shared.PinnedVersions;

        public SharedState<WorldImage> Shared { get; }

        public TestWaiterState WaiterState { get; }

        public ProductionLoop<WorldImage, SimulationProducer, TClock, TWaiter> Loop { get; }

        public ProductionLoop<WorldImage, SimulationProducer, TClock, TWaiter>.TestAccessor Accessor { get; }
    }

    private sealed class TestPinOwner : IPinOwner;
}
