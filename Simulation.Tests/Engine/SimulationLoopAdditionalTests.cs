using Simulation.Engine;
using Simulation.Memory;
using Simulation.World;
using Xunit;

namespace Simulation.Tests.Engine;

public sealed class SimulationLoopAdditionalTests
{
    [Fact]
    public void Tick_CleanupOnSecondFailedRetry_CanFreeStaleSnapshotsAndAllowProgress()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        test.Accessor.Tick(CancellationToken.None);
        test.Accessor.Tick(CancellationToken.None);

        List<WorldSnapshot> drainedSnapshots = test.DrainSnapshots();
        Assert.Equal(5, drainedSnapshots.Count);

        int waitCount = 0;
        test.WaiterState.BeforeWait = () =>
        {
            waitCount++;
            if (waitCount == 1)
                test.Shared.ConsumptionEpoch = 2;
        };

        test.Accessor.Tick(CancellationToken.None);

        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(1),
            ],
            test.WaiterState.WaitCalls);
        Assert.Equal(3, test.Accessor.CurrentTick);
        Assert.Equal(3, test.Accessor.CurrentSnapshot!.Image.TickNumber);
        Assert.Same(test.Accessor.CurrentSnapshot, test.Shared.LatestSnapshot);
    }

    [Fact]
    public void Tick_DoesNotAdvanceStateOrLeakImage_WhenSnapshotIsUnavailableUntilCancellation()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        WorldSnapshot initialSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);

        int imagesAvailableBefore = test.CountAvailableImages();
        List<WorldSnapshot> drainedSnapshots = test.DrainSnapshots();
        Assert.Equal(7, drainedSnapshots.Count);

        using var cancellationSource = new CancellationTokenSource();
        int waitCount = 0;
        test.WaiterState.BeforeWait = () =>
        {
            waitCount++;
            Assert.Equal(imagesAvailableBefore, test.CountAvailableImages());
            if (waitCount == 3)
                cancellationSource.Cancel();
        };

        Assert.Throws<OperationCanceledException>(() => test.Accessor.Tick(cancellationSource.Token));

        Assert.Equal(0, test.Accessor.CurrentTick);
        Assert.Same(initialSnapshot, test.Accessor.CurrentSnapshot);
        Assert.Same(initialSnapshot, test.Shared.LatestSnapshot);
        Assert.Equal(imagesAvailableBefore, test.CountAvailableImages());
        Assert.Equal(0, test.CountAvailableSnapshots());
        Assert.Equal(3, test.WaiterState.WaitCalls.Count);
        Assert.All(test.WaiterState.WaitCalls, wait => Assert.Equal(TimeSpan.FromMilliseconds(1), wait));
    }

    [Fact]
    public void Tick_DoesNotAdvanceStateOrLeakSnapshot_WhenImageIsUnavailableUntilCancellation()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        WorldSnapshot initialSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);

        int snapshotsAvailableBefore = test.CountAvailableSnapshots();
        List<WorldImage> drainedImages = test.DrainImages();
        Assert.Equal(7, drainedImages.Count);

        using var cancellationSource = new CancellationTokenSource();
        int waitCount = 0;
        test.WaiterState.BeforeWait = () =>
        {
            waitCount++;
            Assert.Equal(snapshotsAvailableBefore, test.CountAvailableSnapshots());
            if (waitCount == 3)
                cancellationSource.Cancel();
        };

        Assert.Throws<OperationCanceledException>(() => test.Accessor.Tick(cancellationSource.Token));

        Assert.Equal(0, test.Accessor.CurrentTick);
        Assert.Same(initialSnapshot, test.Accessor.CurrentSnapshot);
        Assert.Same(initialSnapshot, test.Shared.LatestSnapshot);
        Assert.Equal(0, test.CountAvailableImages());
        Assert.Equal(snapshotsAvailableBefore, test.CountAvailableSnapshots());
        Assert.Equal(3, test.WaiterState.WaitCalls.Count);
        Assert.All(test.WaiterState.WaitCalls, wait => Assert.Equal(TimeSpan.FromMilliseconds(1), wait));
    }

    [Fact]
    public void Cleanup_QueuesMultiplePinnedStaleSnapshots_AndDrainsThemAfterUnpin()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        test.Accessor.Tick(CancellationToken.None);
        test.Accessor.Tick(CancellationToken.None);
        test.Accessor.Tick(CancellationToken.None);

        WorldSnapshot tick0 = Assert.IsType<WorldSnapshot>(test.Accessor.OldestSnapshot);
        WorldSnapshot tick1 = Assert.IsType<WorldSnapshot>(tick0.Next);
        WorldSnapshot tick2 = Assert.IsType<WorldSnapshot>(tick1.Next);
        WorldSnapshot tick3 = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);

        test.Memory.PinnedVersions.Pin(tick0.Image.TickNumber);
        test.Memory.PinnedVersions.Pin(tick1.Image.TickNumber);
        test.Shared.ConsumptionEpoch = 3;

        test.Accessor.CleanupStaleSnapshots();

        Assert.Same(tick3, test.Accessor.OldestSnapshot);
        Assert.Same(tick3, test.Accessor.CurrentSnapshot);
        Assert.Equal(2, test.Accessor.PinnedQueueCount);
        Assert.Null(tick0.Next);
        Assert.Null(tick1.Next);
        Assert.True(tick2.IsUnreferenced);

        test.Memory.PinnedVersions.Unpin(tick0.Image.TickNumber);
        test.Memory.PinnedVersions.Unpin(tick1.Image.TickNumber);

        test.Accessor.CleanupStaleSnapshots();

        Assert.Equal(0, test.Accessor.PinnedQueueCount);
    }

    [Fact]
    public void Cleanup_KeepsStillPinnedQueuedSnapshots_AndDrainsOnlyReleasedOnes()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        test.Accessor.Tick(CancellationToken.None);
        test.Accessor.Tick(CancellationToken.None);
        test.Accessor.Tick(CancellationToken.None);

        WorldSnapshot tick0 = Assert.IsType<WorldSnapshot>(test.Accessor.OldestSnapshot);
        WorldSnapshot tick1 = Assert.IsType<WorldSnapshot>(tick0.Next);
        WorldSnapshot tick3 = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);

        test.Memory.PinnedVersions.Pin(tick0.Image.TickNumber);
        test.Memory.PinnedVersions.Pin(tick1.Image.TickNumber);
        test.Shared.ConsumptionEpoch = 3;

        test.Accessor.CleanupStaleSnapshots();

        Assert.Equal(2, test.Accessor.PinnedQueueCount);

        test.Memory.PinnedVersions.Unpin(tick0.Image.TickNumber);
        test.Accessor.CleanupStaleSnapshots();

        Assert.Equal(1, test.Accessor.PinnedQueueCount);
        Assert.True(tick0.IsUnreferenced);
        Assert.False(tick1.IsUnreferenced);
        Assert.Same(tick3, test.Accessor.OldestSnapshot);

        test.Memory.PinnedVersions.Unpin(tick1.Image.TickNumber);
        test.Accessor.CleanupStaleSnapshots();

        Assert.Equal(0, test.Accessor.PinnedQueueCount);
        Assert.True(tick1.IsUnreferenced);
    }

    [Fact]
    public void Cleanup_ThrowsWhenTheSamePinnedSnapshotWouldBeQueuedTwice()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        test.Accessor.Tick(CancellationToken.None);
        test.Accessor.Tick(CancellationToken.None);
        test.Accessor.Tick(CancellationToken.None);

        WorldSnapshot tick0 = Assert.IsType<WorldSnapshot>(test.Accessor.OldestSnapshot);
        WorldSnapshot tick1 = Assert.IsType<WorldSnapshot>(tick0.Next);

        test.Memory.PinnedVersions.Pin(tick0.Image.TickNumber);
        test.Shared.ConsumptionEpoch = 2;
        test.Accessor.CleanupStaleSnapshots();

        Assert.Equal(1, test.Accessor.PinnedQueueCount);

        test.Accessor.SetOldestSnapshotForTesting(tick0);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            test.Accessor.CleanupStaleSnapshots);

        Assert.Contains("more than once", exception.Message);
        Assert.Equal(3, Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot).Image.TickNumber);
    }

    [Fact]
    public void RunOneIteration_WithBucketZeroPressure_OnlyPerformsTheFinalIdleWait()
    {
        var test = SimulationLoopTestContext.CreateManual();

        test.Accessor.Bootstrap();

        long lastTime = 0;
        long accumulator = SimulationConstants.TickDurationNanoseconds;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(1, test.Accessor.CurrentTick);
        Assert.Equal(
            [ TimeSpan.FromMilliseconds(1) ],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_WithFirstPressureBucket_WaitsOneMillisecondThenIdleWait()
    {
        var test = SimulationLoopTestContext.CreateManual();

        test.Accessor.Bootstrap();
        List<WorldSnapshot> drainedSnapshots = test.DrainSnapshots();

        while (test.CountAvailableSnapshots() < 6)
            test.ReturnOneSnapshot(drainedSnapshots);

        long lastTime = 0;
        long accumulator = SimulationConstants.TickDurationNanoseconds;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(1, test.Accessor.CurrentTick);
        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(1),
            ],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_UsesPressureDelayBeforeTick_ThenIdleWait()
    {
        var test = SimulationLoopTestContext.CreateManual();

        test.Accessor.Bootstrap();
        List<WorldSnapshot> drainedSnapshots = test.DrainSnapshots();

        while (test.CountAvailableSnapshots() < 4)
            test.ReturnOneSnapshot(drainedSnapshots);

        long lastTime = 0;
        long accumulator = SimulationConstants.TickDurationNanoseconds;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(1, test.Accessor.CurrentTick);
        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(4),
                TimeSpan.FromMilliseconds(1),
            ],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_UsesPressureDelayForEachDueTick_BeforeFinalIdleWait()
    {
        var test = SimulationLoopTestContext.CreateManual();

        test.Accessor.Bootstrap();
        List<WorldSnapshot> drainedSnapshots = test.DrainSnapshots();

        while (test.CountAvailableSnapshots() < 3)
            test.ReturnOneSnapshot(drainedSnapshots);

        long lastTime = 0;
        long accumulator = SimulationConstants.TickDurationNanoseconds * 2;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(2, test.Accessor.CurrentTick);
        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(8),
                TimeSpan.FromMilliseconds(16),
                TimeSpan.FromMilliseconds(1),
            ],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_ThrowsWhenCancelledDuringNonTrivialPressureDelay()
    {
        var test = SimulationLoopTestContext.CreateManual();

        test.Accessor.Bootstrap();
        List<WorldSnapshot> drainedSnapshots = test.DrainSnapshots();

        while (test.CountAvailableSnapshots() < 4)
            test.ReturnOneSnapshot(drainedSnapshots);

        long lastTime = 0;
        long accumulator = SimulationConstants.TickDurationNanoseconds;

        using var cancellationSource = new CancellationTokenSource();
        test.WaiterState.BeforeWait = cancellationSource.Cancel;

        Assert.Throws<OperationCanceledException>(
            () => test.Accessor.RunOneIteration(cancellationSource.Token, ref lastTime, ref accumulator));

        Assert.Equal(0, test.Accessor.CurrentTick);
        Assert.Equal(SimulationConstants.TickDurationNanoseconds, accumulator);
        Assert.Equal(
            [ TimeSpan.FromMilliseconds(4) ],
            test.WaiterState.WaitCalls);
    }

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
            var loop = new SimulationLoop<TClock, TWaiter>(memory, shared, clock, waiter);
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
            Memory = memory;
            Shared = shared;
            WaiterState = waiterState;
            Accessor = loop.GetTestAccessor();
        }

        public MemorySystem Memory { get; }

        public SharedState Shared { get; }

        public WaiterState WaiterState { get; }

        public SimulationLoop<TClock, TWaiter>.TestAccessor Accessor { get; }

        public List<WorldImage> DrainImages()
        {
            var drained = new List<WorldImage>();

            while (Memory.RentImage() is WorldImage image)
                drained.Add(image);

            return drained;
        }

        public List<WorldSnapshot> DrainSnapshots()
        {
            var drained = new List<WorldSnapshot>();

            while (Memory.RentSnapshot() is WorldSnapshot snapshot)
                drained.Add(snapshot);

            return drained;
        }

        public int CountAvailableImages()
        {
            var rented = new List<WorldImage>();

            while (Memory.RentImage() is WorldImage image)
                rented.Add(image);

            foreach (WorldImage image in rented)
                Memory.ReturnImage(image);

            return rented.Count;
        }

        public int CountAvailableSnapshots()
        {
            var rented = new List<WorldSnapshot>();

            while (Memory.RentSnapshot() is WorldSnapshot snapshot)
                rented.Add(snapshot);

            foreach (WorldSnapshot snapshot in rented)
                Memory.ReturnSnapshot(snapshot);

            return rented.Count;
        }

        public void ReturnOneSnapshot(List<WorldSnapshot> drainedSnapshots)
        {
            Assert.NotEmpty(drainedSnapshots);
            WorldSnapshot snapshot = drainedSnapshots[0];
            drainedSnapshots.RemoveAt(0);
            Memory.ReturnSnapshot(snapshot);
        }
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

        public RecordingWaiter(WaiterState state)
        {
            _state = state;
        }

        public void Wait(TimeSpan duration, CancellationToken cancellationToken)
        {
            _state.WaitCalls.Add(duration);
            _state.BeforeWait?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
