using Simulation.Engine;
using Simulation.Memory;
using Simulation.World;
using Xunit;

namespace Simulation.Tests.Engine;

public sealed class SimulationLoopTickTests
{
    [Fact]
    public void Bootstrap_ThrowsWhenImagePoolIsEmpty()
    {
        var test = SimulationLoopTestContext.Create();

        test.DrainImages();

        Assert.Throws<InvalidOperationException>(test.Accessor.Bootstrap);
    }

    [Fact]
    public void Bootstrap_ThrowsWhenSnapshotPoolIsEmpty()
    {
        var test = SimulationLoopTestContext.Create();

        test.DrainSnapshots();

        Assert.Throws<InvalidOperationException>(test.Accessor.Bootstrap);
    }

    [Fact]
    public void Bootstrap_PublishesTickZeroSnapshot_AsCurrentAndOldest()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();

        WorldSnapshot snapshot = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);

        Assert.Equal(0, test.Accessor.CurrentTick);
        Assert.Same(snapshot, test.Accessor.OldestSnapshot);
        Assert.Same(snapshot, test.Shared.LatestSnapshot);
        Assert.Equal(0, snapshot.Image.TickNumber);
        Assert.Null(snapshot.Next);
    }

    [Fact]
    public void Tick_PublishesNextSnapshot_WhenImageAndSnapshotAreImmediatelyAvailable()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        WorldSnapshot initial = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);

        test.Accessor.Tick(CancellationToken.None);

        Assert.Equal(1, test.Accessor.CurrentTick);
        Assert.NotSame(initial, test.Accessor.CurrentSnapshot);
        Assert.Same(test.Accessor.CurrentSnapshot, test.Shared.LatestSnapshot);
        Assert.Equal(1, test.Accessor.CurrentSnapshot!.Image.TickNumber);
        Assert.Same(test.Accessor.CurrentSnapshot, initial.Next);
    }

    [Fact]
    public void Tick_Retries_WhenImageIsInitiallyUnavailable()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();

        List<WorldImage> drainedImages = test.DrainImages();

        test.WaiterState.BeforeWait = () => test.ReturnOneImage(drainedImages);

        test.Accessor.Tick(CancellationToken.None);

        Assert.Single(test.WaiterState.WaitCalls);
        Assert.Equal(TimeSpan.FromMilliseconds(1), test.WaiterState.WaitCalls[0]);
        Assert.Equal(1, test.Accessor.CurrentTick);
        Assert.Equal(1, test.Accessor.CurrentSnapshot!.Image.TickNumber);
    }

    [Fact]
    public void Tick_ReturnsImageAndRetries_WhenSnapshotIsInitiallyUnavailable()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();

        List<WorldSnapshot> drainedSnapshots = test.DrainSnapshots();

        int imagesAvailableBefore = test.CountAvailableImages();

        test.WaiterState.BeforeWait = () => test.ReturnOneSnapshot(drainedSnapshots);

        test.Accessor.Tick(CancellationToken.None);

        Assert.Single(test.WaiterState.WaitCalls);
        Assert.Equal(TimeSpan.FromMilliseconds(1), test.WaiterState.WaitCalls[0]);
        Assert.Equal(imagesAvailableBefore - 1, test.CountAvailableImages());
        Assert.Equal(1, test.Accessor.CurrentTick);
        Assert.Equal(1, test.Accessor.CurrentSnapshot!.Image.TickNumber);
    }

    [Fact]
    public void Tick_RetriesMultipleTimes_UntilImageAndSnapshotBecomeAvailable()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();

        List<WorldImage> drainedImages = test.DrainImages();
        List<WorldSnapshot> drainedSnapshots = test.DrainSnapshots();

        int waitCount = 0;
        test.WaiterState.BeforeWait = () =>
        {
            waitCount++;
            if (waitCount == 1)
                test.ReturnOneImage(drainedImages);
            else if (waitCount == 2)
                test.ReturnOneSnapshot(drainedSnapshots);
        };

        test.Accessor.Tick(CancellationToken.None);

        Assert.Equal(2, test.WaiterState.WaitCalls.Count);
        Assert.All(test.WaiterState.WaitCalls, wait => Assert.Equal(TimeSpan.FromMilliseconds(1), wait));
        Assert.Equal(1, test.Accessor.CurrentTick);
        Assert.Equal(1, test.Accessor.CurrentSnapshot!.Image.TickNumber);
    }

    [Fact]
    public void Tick_CleanupOnRetry_CanFreeStaleSnapshotsAndAllowProgress()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        test.Accessor.Tick(CancellationToken.None);
        test.Accessor.Tick(CancellationToken.None);

        WorldSnapshot latestBefore = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);
        int imagesAvailableBefore = test.CountAvailableImages();
        List<WorldSnapshot> drainedSnapshots = test.DrainSnapshots();

        Assert.Equal(5, drainedSnapshots.Count);

        test.Shared.ConsumptionEpoch = 2;

        test.Accessor.Tick(CancellationToken.None);

        Assert.Single(test.WaiterState.WaitCalls);
        Assert.Equal(TimeSpan.FromMilliseconds(1), test.WaiterState.WaitCalls[0]);
        Assert.Equal(3, test.Accessor.CurrentTick);
        Assert.Equal(3, test.Accessor.CurrentSnapshot!.Image.TickNumber);
        Assert.NotSame(latestBefore, test.Accessor.CurrentSnapshot);
        Assert.Same(test.Accessor.CurrentSnapshot, test.Shared.LatestSnapshot);
        Assert.Equal(imagesAvailableBefore + 1, test.CountAvailableImages());
    }

    [Fact]
    public void Tick_DoesNotAdvanceState_WhenCancelledDuringRetryWait()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        WorldSnapshot initialSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);

        test.DrainImages();

        using var cancellationSource = new CancellationTokenSource();
        test.WaiterState.BeforeWait = cancellationSource.Cancel;

        Assert.Throws<OperationCanceledException>(() => test.Accessor.Tick(cancellationSource.Token));

        Assert.Equal(0, test.Accessor.CurrentTick);
        Assert.Same(initialSnapshot, test.Accessor.CurrentSnapshot);
        Assert.Same(initialSnapshot, test.Shared.LatestSnapshot);
        Assert.Equal(0, initialSnapshot.Image.TickNumber);
        Assert.Single(test.WaiterState.WaitCalls);
    }

    [Fact]
    public void Cleanup_FreesUnpinnedSnapshotsOlderThanConsumptionEpoch()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        test.Accessor.Tick(CancellationToken.None);
        test.Accessor.Tick(CancellationToken.None);

        WorldSnapshot secondSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);
        WorldSnapshot firstSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.OldestSnapshot);

        test.Shared.ConsumptionEpoch = 2;

        test.Accessor.CleanupStaleSnapshots();

        Assert.NotSame(firstSnapshot, test.Accessor.OldestSnapshot);
        Assert.Same(secondSnapshot, test.Accessor.CurrentSnapshot);
        Assert.Equal(0, test.Accessor.PinnedQueueCount);
    }

    [Fact]
    public void Cleanup_DoesNotFreeTheCurrentSnapshot_EvenWhenConsumptionEpochIsPastIt()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        WorldSnapshot currentSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);

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
        test.Accessor.Tick(CancellationToken.None);
        test.Accessor.Tick(CancellationToken.None);

        test.Shared.ConsumptionEpoch = 1;

        test.Accessor.CleanupStaleSnapshots();

        Assert.Equal(1, Assert.IsType<WorldSnapshot>(test.Accessor.OldestSnapshot).Image.TickNumber);
        Assert.Equal(2, Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot).Image.TickNumber);
        Assert.Equal(0, test.Accessor.PinnedQueueCount);
    }

    [Fact]
    public void Cleanup_DetachesPinnedSnapshotsUntilTheyAreUnpinned()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        test.Accessor.Tick(CancellationToken.None);
        test.Accessor.Tick(CancellationToken.None);

        WorldSnapshot firstSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.OldestSnapshot);
        WorldSnapshot latestSnapshot = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);

        test.Memory.PinnedVersions.Pin(firstSnapshot.Image.TickNumber);
        test.Shared.ConsumptionEpoch = 2;

        test.Accessor.CleanupStaleSnapshots();

        Assert.NotSame(firstSnapshot, test.Accessor.OldestSnapshot);
        Assert.Same(latestSnapshot, test.Accessor.CurrentSnapshot);
        Assert.Equal(1, test.Accessor.PinnedQueueCount);

        test.Memory.PinnedVersions.Unpin(firstSnapshot.Image.TickNumber);

        test.Accessor.CleanupStaleSnapshots();

        Assert.Equal(0, test.Accessor.PinnedQueueCount);
    }

    [Fact]
    public void Cleanup_PinnedMiddleSnapshot_DoesNotBlockLaterSnapshotsFromBeingFreed()
    {
        var test = SimulationLoopTestContext.Create();

        test.Accessor.Bootstrap();
        test.Accessor.Tick(CancellationToken.None); // tick 1
        test.Accessor.Tick(CancellationToken.None); // tick 2
        test.Accessor.Tick(CancellationToken.None); // tick 3

        WorldSnapshot tick0 = Assert.IsType<WorldSnapshot>(test.Accessor.OldestSnapshot);
        WorldSnapshot tick1 = Assert.IsType<WorldSnapshot>(tick0.Next);
        WorldSnapshot tick2 = Assert.IsType<WorldSnapshot>(tick1.Next);
        WorldSnapshot tick3 = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);

        test.Memory.PinnedVersions.Pin(tick1.Image.TickNumber);
        test.Shared.ConsumptionEpoch = 3;

        test.Accessor.CleanupStaleSnapshots();

        Assert.Same(tick3, test.Accessor.CurrentSnapshot);
        Assert.Same(tick3, test.Accessor.OldestSnapshot);
        Assert.Equal(1, test.Accessor.PinnedQueueCount);
        Assert.Null(tick1.Next);
        Assert.Equal(1, tick1.Image.TickNumber);
        Assert.Equal(3, tick3.Image.TickNumber);

        test.Memory.PinnedVersions.Unpin(tick1.Image.TickNumber);
        test.Accessor.CleanupStaleSnapshots();

        Assert.Equal(0, test.Accessor.PinnedQueueCount);
    }

    [Fact]
    public void RunOneIteration_DoesNotTick_WhenAccumulatorIsBelowThreshold()
    {
        var test = SimulationLoopTestContext.CreateManual();

        test.Accessor.Bootstrap();

        long lastTime = 0;
        long accumulator = SimulationConstants.TickDurationNanoseconds - 1;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(0, test.Accessor.CurrentTick);
        Assert.Equal(SimulationConstants.TickDurationNanoseconds - 1, accumulator);
        Assert.Single(test.WaiterState.WaitCalls);
        Assert.Equal(TimeSpan.FromMilliseconds(1), test.WaiterState.WaitCalls[0]);
    }

    [Fact]
    public void RunOneIteration_TicksOnce_WhenAccumulatorReachesThreshold()
    {
        var test = SimulationLoopTestContext.CreateManual();

        test.Accessor.Bootstrap();

        long lastTime = 0;
        long accumulator = SimulationConstants.TickDurationNanoseconds;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(1, test.Accessor.CurrentTick);
        Assert.Equal(0, accumulator);
        Assert.Single(test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_ThrowsWhenCancelledDuringIdleWait()
    {
        var test = SimulationLoopTestContext.CreateManual();

        test.Accessor.Bootstrap();

        long lastTime = 0;
        long accumulator = 0;

        using var cancellationSource = new CancellationTokenSource();
        test.WaiterState.BeforeWait = cancellationSource.Cancel;

        Assert.Throws<OperationCanceledException>(
            () => test.Accessor.RunOneIteration(cancellationSource.Token, ref lastTime, ref accumulator));

        Assert.Equal(0, test.Accessor.CurrentTick);
        Assert.Single(test.WaiterState.WaitCalls);
        Assert.Equal(TimeSpan.FromMilliseconds(1), test.WaiterState.WaitCalls[0]);
    }

    [Fact]
    public void RunOneIteration_ProcessesMultipleTicks_AndPreservesLeftoverAccumulator()
    {
        var test = SimulationLoopTestContext.CreateManual();

        test.Accessor.Bootstrap();

        long lastTime = 0;
        long accumulator = (SimulationConstants.TickDurationNanoseconds * 2) + 123;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(2, test.Accessor.CurrentTick);
        Assert.Equal(123, accumulator);
        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(1),
            ],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_ThrowsWhenCancelledDuringPressureDelayBeforeSecondTick()
    {
        var test = SimulationLoopTestContext.CreateManual();

        test.Accessor.Bootstrap();

        long lastTime = 0;
        long accumulator = (SimulationConstants.TickDurationNanoseconds * 2) + 123;

        using var cancellationSource = new CancellationTokenSource();
        test.WaiterState.BeforeWait = cancellationSource.Cancel;

        Assert.Throws<OperationCanceledException>(
            () => test.Accessor.RunOneIteration(cancellationSource.Token, ref lastTime, ref accumulator));

        Assert.Equal(1, test.Accessor.CurrentTick);
        Assert.Single(test.WaiterState.WaitCalls);
        Assert.Equal(TimeSpan.FromMilliseconds(1), test.WaiterState.WaitCalls[0]);
        Assert.Equal(SimulationConstants.TickDurationNanoseconds + 123, accumulator);
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
            Loop = loop;
            Accessor = loop.GetTestAccessor();
        }

        public MemorySystem Memory { get; }

        public SharedState Shared { get; }

        public WaiterState WaiterState { get; }

        public SimulationLoop<TClock, TWaiter> Loop { get; }

        public SimulationLoop<TClock, TWaiter>.TestAccessor Accessor { get; }

        public List<WorldImage> DrainImages()
        {
            var drained = new List<WorldImage>();

            while (Memory.RentImage() is WorldImage image)
            {
                drained.Add(image);
            }

            return drained;
        }

        public List<WorldSnapshot> DrainSnapshots()
        {
            var drained = new List<WorldSnapshot>();

            while (Memory.RentSnapshot() is WorldSnapshot snapshot)
            {
                drained.Add(snapshot);
            }

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

        public void ReturnOneImage(List<WorldImage> drainedImages)
        {
            Assert.NotEmpty(drainedImages);
            WorldImage image = drainedImages[0];
            drainedImages.RemoveAt(0);
            Memory.ReturnImage(image);
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
