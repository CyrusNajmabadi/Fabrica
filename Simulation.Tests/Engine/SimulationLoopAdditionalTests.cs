using Simulation.Engine;
using Simulation.Memory;
using Simulation.World;
using Xunit;

namespace Simulation.Tests.Engine;

public sealed class SimulationLoopAdditionalTests
{
    private static int LowWaterMarkTicks =>
        (int)(SimulationConstants.PressureLowWaterMarkNanoseconds / SimulationConstants.TickDurationNanoseconds);

    [Fact]
    public void Cleanup_QueuesMultiplePinnedStaleSnapshots_AndDrainsThemAfterUnpin()
    {
        var test = SimulationLoopTestContext.Create();
        object tick0Owner = new();
        object tick1Owner = new();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();
        test.Accessor.Tick();

        var tick0 = Assert.IsType<WorldSnapshot>(test.Accessor.OldestSnapshot);
        var tick1 = Assert.IsType<WorldSnapshot>(tick0.Next);
        var tick2 = Assert.IsType<WorldSnapshot>(tick1.Next);
        var tick3 = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);

        test.Memory.PinnedVersions.Pin(tick0.TickNumber, tick0Owner);
        test.Memory.PinnedVersions.Pin(tick1.TickNumber, tick1Owner);
        test.Shared.ConsumptionEpoch = 3;

        test.Accessor.CleanupStaleSnapshots();

        Assert.Same(tick3, test.Accessor.OldestSnapshot);
        Assert.Same(tick3, test.Accessor.CurrentSnapshot);
        Assert.Equal(2, test.Accessor.PinnedQueueCount);
        Assert.Null(tick0.Next);
        Assert.Null(tick1.Next);
        Assert.True(tick2.IsUnreferenced);

        test.Memory.PinnedVersions.Unpin(tick0.TickNumber, tick0Owner);
        test.Memory.PinnedVersions.Unpin(tick1.TickNumber, tick1Owner);

        test.Accessor.CleanupStaleSnapshots();

        Assert.Equal(0, test.Accessor.PinnedQueueCount);
    }

    [Fact]
    public void Cleanup_KeepsStillPinnedQueuedSnapshots_AndDrainsOnlyReleasedOnes()
    {
        var test = SimulationLoopTestContext.Create();
        object tick0Owner = new();
        object tick1Owner = new();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();
        test.Accessor.Tick();

        var tick0 = Assert.IsType<WorldSnapshot>(test.Accessor.OldestSnapshot);
        var tick1 = Assert.IsType<WorldSnapshot>(tick0.Next);
        var tick3 = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);

        test.Memory.PinnedVersions.Pin(tick0.TickNumber, tick0Owner);
        test.Memory.PinnedVersions.Pin(tick1.TickNumber, tick1Owner);
        test.Shared.ConsumptionEpoch = 3;

        test.Accessor.CleanupStaleSnapshots();

        Assert.Equal(2, test.Accessor.PinnedQueueCount);

        test.Memory.PinnedVersions.Unpin(tick0.TickNumber, tick0Owner);
        test.Accessor.CleanupStaleSnapshots();

        Assert.Equal(1, test.Accessor.PinnedQueueCount);
        Assert.True(tick0.IsUnreferenced);
        Assert.False(tick1.IsUnreferenced);
        Assert.Same(tick3, test.Accessor.OldestSnapshot);

        test.Memory.PinnedVersions.Unpin(tick1.TickNumber, tick1Owner);
        test.Accessor.CleanupStaleSnapshots();

        Assert.Equal(0, test.Accessor.PinnedQueueCount);
        Assert.True(tick1.IsUnreferenced);
    }

    [Fact]
    public void Cleanup_ThrowsWhenTheSamePinnedSnapshotWouldBeQueuedTwice()
    {
        var test = SimulationLoopTestContext.Create();
        object pinOwner = new();

        test.Accessor.Bootstrap();
        test.Accessor.Tick();
        test.Accessor.Tick();
        test.Accessor.Tick();

        var tick0 = Assert.IsType<WorldSnapshot>(test.Accessor.OldestSnapshot);

        test.Memory.PinnedVersions.Pin(tick0.TickNumber, pinOwner);
        test.Shared.ConsumptionEpoch = 2;
        test.Accessor.CleanupStaleSnapshots();

        Assert.Equal(1, test.Accessor.PinnedQueueCount);

        test.Accessor.SetOldestSnapshotForTesting(tick0);

        var exception = Assert.Throws<InvalidOperationException>(
            test.Accessor.CleanupStaleSnapshots);

        Assert.Contains("more than once", exception.Message);
        Assert.Equal(3, Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot).TickNumber);
    }

    [Fact]
    public void RunOneIteration_WithNoPressure_OnlyPerformsTheFinalIdleWait()
    {
        var test = SimulationLoopTestContext.CreateManual();

        test.Accessor.Bootstrap();

        long lastTime = 0;
        var accumulator = SimulationConstants.TickDurationNanoseconds;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(1, test.Accessor.CurrentTick);
        Assert.Equal(
            [GetIdleYieldWait()],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_WithFirstPressureBucket_WaitsBaseDelayThenIdleWait()
    {
        var test = SimulationLoopTestContext.CreateManual();

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
    public void RunOneIteration_UsesPressureDelayBeforeTick_ThenIdleWait()
    {
        var test = SimulationLoopTestContext.CreateManual();

        test.Accessor.Bootstrap();
        for (var i = 0; i < LowWaterMarkTicks + 2; i++)
            test.Accessor.Tick();

        test.WaiterState.WaitCalls.Clear();

        long lastTime = 0;
        var accumulator = SimulationConstants.TickDurationNanoseconds;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        var tickBefore = LowWaterMarkTicks + 2;
        Assert.Equal(tickBefore + 1, test.Accessor.CurrentTick);
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
        var test = SimulationLoopTestContext.CreateManual();

        test.Accessor.Bootstrap();
        for (var i = 0; i < LowWaterMarkTicks + 1; i++)
            test.Accessor.Tick();

        test.WaiterState.WaitCalls.Clear();

        long lastTime = 0;
        var accumulator = SimulationConstants.TickDurationNanoseconds * 2;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        var tickBefore = LowWaterMarkTicks + 1;
        Assert.Equal(tickBefore + 2, test.Accessor.CurrentTick);
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
        var test = SimulationLoopTestContext.CreateManual();

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
        public static SimulationLoopTestContext<FakeClock, RecordingWaiter> Create()
        {
            var waiterState = new WaiterState();
            return Create(
                clock: new FakeClock(),
                waiter: new RecordingWaiter(waiterState),
                waiterState: waiterState);
        }

        public static SimulationLoopTestContext<ManualClock, RecordingWaiter> CreateManual()
        {
            var waiterState = new WaiterState();
            return Create(
                clock: new ManualClock(),
                waiter: new RecordingWaiter(waiterState),
                waiterState: waiterState);
        }

        public static SimulationLoopTestContext<TClock, TWaiter> Create<TClock, TWaiter>(
            TClock clock,
            TWaiter waiter,
            WaiterState waiterState,
            int poolSize = 8)
            where TClock : struct, IClock
            where TWaiter : struct, IWaiter
        {
            var memory = new MemorySystem(poolSize);
            var shared = new SharedState();
            var loop = new SimulationLoop<TClock, TWaiter>(memory, shared, new Simulator(0), clock, waiter);
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
            WaiterState waiterState,
            SimulationLoop<TClock, TWaiter> loop)
        {
            this.Memory = memory;
            this.Shared = shared;
            this.WaiterState = waiterState;
            this.Accessor = loop.GetTestAccessor();
        }

        public MemorySystem Memory { get; }

        public SharedState Shared { get; }

        public WaiterState WaiterState { get; }

        public SimulationLoop<TClock, TWaiter>.TestAccessor Accessor { get; }
    }

    internal readonly struct FakeClock : IClock
    {
        public long NowNanoseconds => 0;
    }

    internal readonly struct ManualClock : IClock
    {
        public long NowNanoseconds => 0;
    }

    internal sealed class WaiterState
    {
        public readonly List<TimeSpan> WaitCalls = [];

        public Action? BeforeWait { get; set; }
    }

    internal readonly struct RecordingWaiter : IWaiter
    {
        private readonly WaiterState _state;

        public RecordingWaiter(WaiterState state) => _state = state;

        public void Wait(TimeSpan duration, CancellationToken cancellationToken)
        {
            _state.WaitCalls.Add(duration);
            _state.BeforeWait?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
