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

        var snapshot = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);

        Assert.Equal(0, test.Accessor.CurrentTick);
        Assert.Same(snapshot, test.Accessor.OldestSnapshot);
        Assert.Same(snapshot, test.Shared.LatestSnapshot);
        Assert.Equal(0, snapshot.TickNumber);
        Assert.Null(snapshot.Next);
    }

    [Fact]
    public void Tick_PublishesNextSnapshot()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        var initial = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);

        test.Accessor.Tick();

        Assert.Equal(1, test.Accessor.CurrentTick);
        Assert.NotSame(initial, test.Accessor.CurrentSnapshot);
        Assert.Same(test.Accessor.CurrentSnapshot, test.Shared.LatestSnapshot);
        Assert.Equal(1, test.Accessor.CurrentSnapshot!.TickNumber);
        Assert.Same(test.Accessor.CurrentSnapshot, initial.Next);
    }

    [Fact]
    public void Cleanup_FreesUnpinnedSnapshotsOlderThanConsumptionEpoch()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();

        var secondSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);
        var firstSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.OldestSnapshot);

        test.Shared.ConsumptionEpoch = 2;

        test.Accessor.CleanupStaleSnapshots();

        Assert.NotSame(firstSnapshot, test.Accessor.OldestSnapshot);
        Assert.Same(secondSnapshot, test.Accessor.CurrentSnapshot);
        Assert.Equal(0, test.Accessor.PinnedQueueCount);
    }

    [Fact]
    public void Cleanup_ResetsFreedImageBeforeItCanBeReused()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();

        var firstSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.OldestSnapshot);

        test.Shared.ConsumptionEpoch = 2;

        test.Accessor.CleanupStaleSnapshots();

        Assert.True(firstSnapshot.IsUnreferenced);
    }

    [Fact]
    public void Cleanup_DoesNotFreeTheCurrentSnapshot_EvenWhenConsumptionEpochIsPastIt()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        var currentSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);

        test.Shared.ConsumptionEpoch = 100;

        test.Accessor.CleanupStaleSnapshots();

        Assert.Same(currentSnapshot, test.Accessor.CurrentSnapshot);
        Assert.Same(currentSnapshot, test.Accessor.OldestSnapshot);
        Assert.Same(currentSnapshot, test.Shared.LatestSnapshot);
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

        test.Accessor.CleanupStaleSnapshots();

        Assert.Equal(1, Assert.IsType<WorldSnapshot>(test.Accessor.OldestSnapshot).TickNumber);
        Assert.Equal(2, Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot).TickNumber);
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

        var firstSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.OldestSnapshot);
        var latestSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);

        test.Memory.PinnedVersions.Pin(firstSnapshot.TickNumber, pinOwner);
        test.Shared.ConsumptionEpoch = 2;

        test.Accessor.CleanupStaleSnapshots();

        Assert.NotSame(firstSnapshot, test.Accessor.OldestSnapshot);
        Assert.Same(latestSnapshot, test.Accessor.CurrentSnapshot);
        Assert.Equal(1, test.Accessor.PinnedQueueCount);

        test.Memory.PinnedVersions.Unpin(firstSnapshot.TickNumber, pinOwner);

        test.Accessor.CleanupStaleSnapshots();

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

        var tick0 = Assert.IsType<WorldSnapshot>(test.Accessor.OldestSnapshot);
        var tick1 = Assert.IsType<WorldSnapshot>(tick0.Next);
        var tick2 = Assert.IsType<WorldSnapshot>(tick1.Next);
        var tick3 = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);

        test.Memory.PinnedVersions.Pin(tick1.TickNumber, pinOwner);
        test.Shared.ConsumptionEpoch = 3;

        test.Accessor.CleanupStaleSnapshots();

        Assert.Same(tick3, test.Accessor.CurrentSnapshot);
        Assert.Same(tick3, test.Accessor.OldestSnapshot);
        Assert.Equal(1, test.Accessor.PinnedQueueCount);
        Assert.Null(tick1.Next);
        Assert.Equal(1, tick1.TickNumber);
        Assert.Equal(3, tick3.TickNumber);

        test.Memory.PinnedVersions.Unpin(tick1.TickNumber, pinOwner);
        test.Accessor.CleanupStaleSnapshots();

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

        Assert.Equal(0, test.Accessor.CurrentTick);
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

        Assert.Equal(1, test.Accessor.CurrentTick);
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

        Assert.Equal(0, test.Accessor.CurrentTick);
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

        Assert.Equal(2, test.Accessor.CurrentTick);
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
        Assert.Equal(tickBefore + 1, test.Accessor.CurrentTick);
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

        var tickBefore = test.Accessor.CurrentTick;
        test.WaiterState.WaitCalls.Clear();

        long lastTime = 0;
        var accumulator = SimulationConstants.TickDurationNanoseconds;

        using var cancellationSource = new CancellationTokenSource();
        test.WaiterState.BeforeWait = cancellationSource.Cancel;

        Assert.Throws<OperationCanceledException>(
            () => test.Accessor.RunOneIteration(cancellationSource.Token, ref lastTime, ref accumulator));

        Assert.Equal(tickBefore, test.Accessor.CurrentTick);
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

        Assert.Equal(HardCeilingTicks + 1, test.Accessor.CurrentTick);

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

        var tickBefore = test.Accessor.CurrentTick;
        test.WaiterState.WaitCalls.Clear();

        using var cancellationSource = new CancellationTokenSource();
        test.WaiterState.BeforeWait = cancellationSource.Cancel;

        long lastTime = 0;
        var accumulator = SimulationConstants.TickDurationNanoseconds;

        Assert.Throws<OperationCanceledException>(
            () => test.Accessor.RunOneIteration(cancellationSource.Token, ref lastTime, ref accumulator));

        Assert.Equal(tickBefore, test.Accessor.CurrentTick);
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
        public static SimulationLoopTestContext<FakeClock, RecordingWaiter> Create()
        {
            var waiterState = new Simulation.Tests.Helpers.WaiterState();
            return Create(
                clock: new FakeClock(),
                waiter: new RecordingWaiter(waiterState),
                waiterState: waiterState);
        }

        public static SimulationLoopTestContext<TClock, TWaiter> Create<TClock, TWaiter>(
            TClock clock,
            TWaiter waiter,
            Simulation.Tests.Helpers.WaiterState waiterState,
            int poolSize = 8)
            where TClock : struct, IClock
            where TWaiter : struct, IWaiter
        {
            var memory = new MemorySystem(poolSize);
            var shared = new SharedState();
            var loop = new SimulationLoop<TClock, TWaiter>(memory, shared, new Simulator(1), clock, waiter);
            return new SimulationLoopTestContext<TClock, TWaiter>(memory, shared, waiterState, loop);
        }
    }

    private sealed class SimulationLoopTestContext<TClock, TWaiter>
        where TClock : struct, IClock
        where TWaiter : struct, IWaiter
    {
        internal SimulationLoopTestContext(
            MemorySystem memory,
            SharedState shared,
            Simulation.Tests.Helpers.WaiterState waiterState,
            SimulationLoop<TClock, TWaiter> loop)
        {
            this.Memory = memory;
            this.Shared = shared;
            this.WaiterState = waiterState;
            this.Loop = loop;
            this.Accessor = loop.GetTestAccessor();
        }

        public MemorySystem Memory { get; }

        public SharedState Shared { get; }

        public Simulation.Tests.Helpers.WaiterState WaiterState { get; }

        public SimulationLoop<TClock, TWaiter> Loop { get; }

        public SimulationLoop<TClock, TWaiter>.TestAccessor Accessor { get; }
    }
}
