using Simulation.Engine;
using Simulation.Memory;
using Simulation.Tests.Helpers;
using Simulation.World;
using Xunit;

namespace Simulation.Tests.Engine;

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

        var snapshot = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentNode);

        Assert.Equal(0, test.Accessor.CurrentSequence);
        Assert.Same(snapshot, test.Accessor.OldestNode);
        Assert.Same(snapshot, test.Shared.LatestNode);
        Assert.Equal(0, snapshot.TickNumber);
        Assert.Null(snapshot.NextInChain);
    }

    [Fact]
    public void Tick_PublishesNextSnapshot()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        var initial = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentNode);

        test.Accessor.Tick();

        Assert.Equal(1, test.Accessor.CurrentSequence);
        Assert.NotSame(initial, test.Accessor.CurrentNode);
        Assert.Same(test.Accessor.CurrentNode, test.Shared.LatestNode);
        Assert.Equal(1, test.Accessor.CurrentNode!.TickNumber);
        Assert.Same(test.Accessor.CurrentNode, initial.NextInChain);
    }

    [Fact]
    public void Cleanup_FreesUnpinnedSnapshotsOlderThanConsumptionEpoch()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();

        var secondSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentNode);
        var firstSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.OldestNode);

        test.Shared.ConsumptionEpoch = 2;

        test.Accessor.CleanupStaleNodes();

        Assert.NotSame(firstSnapshot, test.Accessor.OldestNode);
        Assert.Same(secondSnapshot, test.Accessor.CurrentNode);
        Assert.Equal(0, test.Accessor.PinnedQueueCount);
    }

    [Fact]
    public void Cleanup_ResetsFreedImageBeforeItCanBeReused()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();

        var firstSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.OldestNode);

        test.Shared.ConsumptionEpoch = 2;

        test.Accessor.CleanupStaleNodes();

        Assert.True(firstSnapshot.IsUnreferenced);
    }

    [Fact]
    public void Cleanup_DoesNotFreeTheCurrentSnapshot_EvenWhenConsumptionEpochIsPastIt()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        var currentSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentNode);

        test.Shared.ConsumptionEpoch = 100;

        test.Accessor.CleanupStaleNodes();

        Assert.Same(currentSnapshot, test.Accessor.CurrentNode);
        Assert.Same(currentSnapshot, test.Accessor.OldestNode);
        Assert.Same(currentSnapshot, test.Shared.LatestNode);
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

        Assert.Equal(1, Assert.IsType<WorldSnapshot>(test.Accessor.OldestNode).TickNumber);
        Assert.Equal(2, Assert.IsType<WorldSnapshot>(test.Accessor.CurrentNode).TickNumber);
        Assert.Equal(0, test.Accessor.PinnedQueueCount);
    }

    [Fact]
    public void Cleanup_DetachesPinnedSnapshotsUntilTheyAreUnpinned()
    {
        var test = SimulationLoopTestContext.Create();
        object pinOwner = new();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();

        var firstSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.OldestNode);
        var latestSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentNode);

        test.PinnedVersions.Pin(firstSnapshot.TickNumber, pinOwner);
        test.Shared.ConsumptionEpoch = 2;

        test.Accessor.CleanupStaleNodes();

        Assert.NotSame(firstSnapshot, test.Accessor.OldestNode);
        Assert.Same(latestSnapshot, test.Accessor.CurrentNode);
        Assert.Equal(1, test.Accessor.PinnedQueueCount);

        test.PinnedVersions.Unpin(firstSnapshot.TickNumber, pinOwner);

        test.Accessor.CleanupStaleNodes();

        Assert.Equal(0, test.Accessor.PinnedQueueCount);
    }

    [Fact]
    public void Cleanup_PinnedMiddleSnapshot_DoesNotBlockLaterSnapshotsFromBeingFreed()
    {
        var test = SimulationLoopTestContext.Create();
        object pinOwner = new();

        test.Accessor.Bootstrap();
        test.Accessor.Tick(); // tick 1
        test.Accessor.Tick(); // tick 2
        test.Accessor.Tick(); // tick 3

        var tick0 = Assert.IsType<WorldSnapshot>(test.Accessor.OldestNode);
        var tick1 = Assert.IsType<WorldSnapshot>(tick0.NextInChain);
        var tick2 = Assert.IsType<WorldSnapshot>(tick1.NextInChain);
        var tick3 = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentNode);

        test.PinnedVersions.Pin(tick1.TickNumber, pinOwner);
        test.Shared.ConsumptionEpoch = 3;

        test.Accessor.CleanupStaleNodes();

        Assert.Same(tick3, test.Accessor.CurrentNode);
        Assert.Same(tick3, test.Accessor.OldestNode);
        Assert.Equal(1, test.Accessor.PinnedQueueCount);
        Assert.Null(tick1.NextInChain);
        Assert.Equal(1, tick1.TickNumber);
        Assert.Equal(3, tick3.TickNumber);

        test.PinnedVersions.Unpin(tick1.TickNumber, pinOwner);
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
            var snapshotPool = new ObjectPool<WorldSnapshot>(poolSize);
            var imagePool = new ObjectPool<WorldImage>(poolSize);
            var pinnedVersions = new PinnedVersions();
            var shared = new SharedState<WorldSnapshot>();
            var producer = new SimulationProducer(imagePool, new SimulationCoordinator(1));
            var loop = new ProductionLoop<WorldSnapshot, SimulationProducer, TClock, TWaiter>(
                snapshotPool, pinnedVersions, shared, producer, clock, waiter);
            return new SimulationLoopTestContext<TClock, TWaiter>(pinnedVersions, shared, waiterState, loop);
        }
    }

    private sealed class SimulationLoopTestContext<TClock, TWaiter>
        where TClock : struct, IClock
        where TWaiter : struct, IWaiter
    {
        internal SimulationLoopTestContext(
            PinnedVersions pinnedVersions,
            SharedState<WorldSnapshot> shared,
            TestWaiterState waiterState,
            ProductionLoop<WorldSnapshot, SimulationProducer, TClock, TWaiter> loop)
        {
            this.PinnedVersions = pinnedVersions;
            this.Shared = shared;
            this.WaiterState = waiterState;
            this.Loop = loop;
            this.Accessor = loop.GetTestAccessor();
        }

        public PinnedVersions PinnedVersions { get; }

        public SharedState<WorldSnapshot> Shared { get; }

        public TestWaiterState WaiterState { get; }

        public ProductionLoop<WorldSnapshot, SimulationProducer, TClock, TWaiter> Loop { get; }

        public ProductionLoop<WorldSnapshot, SimulationProducer, TClock, TWaiter>.TestAccessor Accessor { get; }
    }
}
