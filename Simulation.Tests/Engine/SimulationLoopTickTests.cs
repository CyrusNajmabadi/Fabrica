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
        var memory = new MemorySystem(poolSize: 8);
        var shared = new SharedState();
        var loop = new SimulationLoop<FakeClock, RecordingWaiter>(memory, shared, new FakeClock(), new RecordingWaiter());
        var accessor = loop.GetTestAccessor();

        DrainImages(memory);

        Assert.Throws<InvalidOperationException>(accessor.Bootstrap);
    }

    [Fact]
    public void Bootstrap_ThrowsWhenSnapshotPoolIsEmpty()
    {
        var memory = new MemorySystem(poolSize: 8);
        var shared = new SharedState();
        var loop = new SimulationLoop<FakeClock, RecordingWaiter>(memory, shared, new FakeClock(), new RecordingWaiter());
        var accessor = loop.GetTestAccessor();

        DrainSnapshots(memory);

        Assert.Throws<InvalidOperationException>(accessor.Bootstrap);
    }

    [Fact]
    public void Tick_PublishesNextSnapshot_WhenImageAndSnapshotAreImmediatelyAvailable()
    {
        var memory = new MemorySystem(poolSize: 8);
        var shared = new SharedState();
        var loop = new SimulationLoop<FakeClock, RecordingWaiter>(memory, shared, new FakeClock(), new RecordingWaiter());
        var accessor = loop.GetTestAccessor();

        accessor.Bootstrap();
        WorldSnapshot initial = Assert.IsType<WorldSnapshot>(accessor.CurrentSnapshot);

        accessor.Tick(CancellationToken.None);

        Assert.Equal(1, accessor.CurrentTick);
        Assert.NotSame(initial, accessor.CurrentSnapshot);
        Assert.Same(accessor.CurrentSnapshot, shared.LatestSnapshot);
        Assert.Equal(1, accessor.CurrentSnapshot!.Image.TickNumber);
        Assert.Same(accessor.CurrentSnapshot, initial.Next);
    }

    [Fact]
    public void Tick_Retries_WhenImageIsInitiallyUnavailable()
    {
        var memory = new MemorySystem(poolSize: 8);
        var shared = new SharedState();
        var waiterState = new WaiterState();
        var waiter = new RecordingWaiter(waiterState);
        var loop = new SimulationLoop<FakeClock, RecordingWaiter>(memory, shared, new FakeClock(), waiter);
        var accessor = loop.GetTestAccessor();

        accessor.Bootstrap();

        List<WorldImage> drainedImages = DrainImages(memory);

        waiterState.BeforeWait = () => ReturnOneImage(memory, drainedImages);

        accessor.Tick(CancellationToken.None);

        Assert.Single(waiterState.WaitCalls);
        Assert.Equal(TimeSpan.FromMilliseconds(1), waiterState.WaitCalls[0]);
        Assert.Equal(1, accessor.CurrentTick);
        Assert.Equal(1, accessor.CurrentSnapshot!.Image.TickNumber);
    }

    [Fact]
    public void Tick_ReturnsImageAndRetries_WhenSnapshotIsInitiallyUnavailable()
    {
        var memory = new MemorySystem(poolSize: 8);
        var shared = new SharedState();
        var waiterState = new WaiterState();
        var waiter = new RecordingWaiter(waiterState);
        var loop = new SimulationLoop<FakeClock, RecordingWaiter>(memory, shared, new FakeClock(), waiter);
        var accessor = loop.GetTestAccessor();

        accessor.Bootstrap();

        List<WorldSnapshot> drainedSnapshots = DrainSnapshots(memory);

        int imagesAvailableBefore = CountAvailableImages(memory);

        waiterState.BeforeWait = () => ReturnOneSnapshot(memory, drainedSnapshots);

        accessor.Tick(CancellationToken.None);

        Assert.Single(waiterState.WaitCalls);
        Assert.Equal(TimeSpan.FromMilliseconds(1), waiterState.WaitCalls[0]);
        Assert.Equal(imagesAvailableBefore - 1, CountAvailableImages(memory));
        Assert.Equal(1, accessor.CurrentTick);
        Assert.Equal(1, accessor.CurrentSnapshot!.Image.TickNumber);
    }

    [Fact]
    public void Tick_RetriesMultipleTimes_UntilImageAndSnapshotBecomeAvailable()
    {
        var memory = new MemorySystem(poolSize: 8);
        var shared = new SharedState();
        var waiterState = new WaiterState();
        var waiter = new RecordingWaiter(waiterState);
        var loop = new SimulationLoop<FakeClock, RecordingWaiter>(memory, shared, new FakeClock(), waiter);
        var accessor = loop.GetTestAccessor();

        accessor.Bootstrap();

        List<WorldImage> drainedImages = DrainImages(memory);
        List<WorldSnapshot> drainedSnapshots = DrainSnapshots(memory);

        int waitCount = 0;
        waiterState.BeforeWait = () =>
        {
            waitCount++;
            if (waitCount == 1)
                ReturnOneImage(memory, drainedImages);
            else if (waitCount == 2)
                ReturnOneSnapshot(memory, drainedSnapshots);
        };

        accessor.Tick(CancellationToken.None);

        Assert.Equal(2, waiterState.WaitCalls.Count);
        Assert.All(waiterState.WaitCalls, wait => Assert.Equal(TimeSpan.FromMilliseconds(1), wait));
        Assert.Equal(1, accessor.CurrentTick);
        Assert.Equal(1, accessor.CurrentSnapshot!.Image.TickNumber);
    }

    [Fact]
    public void Tick_DoesNotAdvanceState_WhenCancelledDuringRetryWait()
    {
        var memory = new MemorySystem(poolSize: 8);
        var shared = new SharedState();
        var waiterState = new WaiterState();
        var waiter = new RecordingWaiter(waiterState);
        var loop = new SimulationLoop<FakeClock, RecordingWaiter>(memory, shared, new FakeClock(), waiter);
        var accessor = loop.GetTestAccessor();

        accessor.Bootstrap();
        WorldSnapshot initialSnapshot = Assert.IsType<WorldSnapshot>(accessor.CurrentSnapshot);

        DrainImages(memory);

        using var cancellationSource = new CancellationTokenSource();
        waiterState.BeforeWait = cancellationSource.Cancel;

        Assert.Throws<OperationCanceledException>(() => accessor.Tick(cancellationSource.Token));

        Assert.Equal(0, accessor.CurrentTick);
        Assert.Same(initialSnapshot, accessor.CurrentSnapshot);
        Assert.Same(initialSnapshot, shared.LatestSnapshot);
        Assert.Equal(0, initialSnapshot.Image.TickNumber);
        Assert.Single(waiterState.WaitCalls);
    }

    [Fact]
    public void Cleanup_FreesUnpinnedSnapshotsOlderThanConsumptionEpoch()
    {
        var memory = new MemorySystem(poolSize: 8);
        var shared = new SharedState();
        var loop = new SimulationLoop<FakeClock, RecordingWaiter>(memory, shared, new FakeClock(), new RecordingWaiter());
        var accessor = loop.GetTestAccessor();

        accessor.Bootstrap();
        accessor.Tick(CancellationToken.None);
        accessor.Tick(CancellationToken.None);

        WorldSnapshot secondSnapshot = Assert.IsType<WorldSnapshot>(accessor.CurrentSnapshot);
        WorldSnapshot firstSnapshot = Assert.IsType<WorldSnapshot>(accessor.OldestSnapshot);

        shared.ConsumptionEpoch = 2;

        accessor.CleanupStaleSnapshots();

        Assert.NotSame(firstSnapshot, accessor.OldestSnapshot);
        Assert.Same(secondSnapshot, accessor.CurrentSnapshot);
        Assert.Equal(0, accessor.PinnedQueueCount);
    }

    [Fact]
    public void Cleanup_DoesNotFreeSnapshot_AtConsumptionEpochBoundary()
    {
        var memory = new MemorySystem(poolSize: 8);
        var shared = new SharedState();
        var loop = new SimulationLoop<FakeClock, RecordingWaiter>(memory, shared, new FakeClock(), new RecordingWaiter());
        var accessor = loop.GetTestAccessor();

        accessor.Bootstrap();
        accessor.Tick(CancellationToken.None);
        accessor.Tick(CancellationToken.None);

        shared.ConsumptionEpoch = 1;

        accessor.CleanupStaleSnapshots();

        Assert.Equal(1, Assert.IsType<WorldSnapshot>(accessor.OldestSnapshot).Image.TickNumber);
        Assert.Equal(2, Assert.IsType<WorldSnapshot>(accessor.CurrentSnapshot).Image.TickNumber);
        Assert.Equal(0, accessor.PinnedQueueCount);
    }

    [Fact]
    public void Cleanup_DetachesPinnedSnapshotsUntilTheyAreUnpinned()
    {
        var memory = new MemorySystem(poolSize: 8);
        var shared = new SharedState();
        var loop = new SimulationLoop<FakeClock, RecordingWaiter>(memory, shared, new FakeClock(), new RecordingWaiter());
        var accessor = loop.GetTestAccessor();

        accessor.Bootstrap();
        accessor.Tick(CancellationToken.None);
        accessor.Tick(CancellationToken.None);

        WorldSnapshot firstSnapshot = Assert.IsType<WorldSnapshot>(accessor.OldestSnapshot);
        WorldSnapshot latestSnapshot = Assert.IsType<WorldSnapshot>(accessor.CurrentSnapshot);

        memory.PinnedVersions.Pin(firstSnapshot.Image.TickNumber);
        shared.ConsumptionEpoch = 2;

        accessor.CleanupStaleSnapshots();

        Assert.NotSame(firstSnapshot, accessor.OldestSnapshot);
        Assert.Same(latestSnapshot, accessor.CurrentSnapshot);
        Assert.Equal(1, accessor.PinnedQueueCount);

        memory.PinnedVersions.Unpin(firstSnapshot.Image.TickNumber);

        accessor.CleanupStaleSnapshots();

        Assert.Equal(0, accessor.PinnedQueueCount);
    }

    [Fact]
    public void Cleanup_PinnedMiddleSnapshot_DoesNotBlockLaterSnapshotsFromBeingFreed()
    {
        var memory = new MemorySystem(poolSize: 8);
        var shared = new SharedState();
        var loop = new SimulationLoop<FakeClock, RecordingWaiter>(memory, shared, new FakeClock(), new RecordingWaiter());
        var accessor = loop.GetTestAccessor();

        accessor.Bootstrap();
        accessor.Tick(CancellationToken.None); // tick 1
        accessor.Tick(CancellationToken.None); // tick 2
        accessor.Tick(CancellationToken.None); // tick 3

        WorldSnapshot tick0 = Assert.IsType<WorldSnapshot>(accessor.OldestSnapshot);
        WorldSnapshot tick1 = Assert.IsType<WorldSnapshot>(tick0.Next);
        WorldSnapshot tick2 = Assert.IsType<WorldSnapshot>(tick1.Next);
        WorldSnapshot tick3 = Assert.IsType<WorldSnapshot>(accessor.CurrentSnapshot);

        memory.PinnedVersions.Pin(tick1.Image.TickNumber);
        shared.ConsumptionEpoch = 3;

        accessor.CleanupStaleSnapshots();

        Assert.Same(tick3, accessor.CurrentSnapshot);
        Assert.Same(tick3, accessor.OldestSnapshot);
        Assert.Equal(1, accessor.PinnedQueueCount);
        Assert.Null(tick1.Next);
        Assert.Equal(1, tick1.Image.TickNumber);
        Assert.Equal(3, tick3.Image.TickNumber);

        memory.PinnedVersions.Unpin(tick1.Image.TickNumber);
        accessor.CleanupStaleSnapshots();

        Assert.Equal(0, accessor.PinnedQueueCount);
    }

    private static List<WorldImage> DrainImages(MemorySystem memory)
    {
        var drained = new List<WorldImage>();

        while (memory.RentImage() is WorldImage image)
        {
            drained.Add(image);
        }

        return drained;
    }

    private static List<WorldSnapshot> DrainSnapshots(MemorySystem memory)
    {
        var drained = new List<WorldSnapshot>();

        while (memory.RentSnapshot() is WorldSnapshot snapshot)
        {
            drained.Add(snapshot);
        }

        return drained;
    }

    private static int CountAvailableImages(MemorySystem memory)
    {
        var rented = new List<WorldImage>();

        while (memory.RentImage() is WorldImage image)
            rented.Add(image);

        foreach (WorldImage image in rented)
            memory.ReturnImage(image);

        return rented.Count;
    }

    private static void ReturnOneImage(MemorySystem memory, List<WorldImage> drainedImages)
    {
        Assert.NotEmpty(drainedImages);
        WorldImage image = drainedImages[0];
        drainedImages.RemoveAt(0);
        memory.ReturnImage(image);
    }

    private static void ReturnOneSnapshot(MemorySystem memory, List<WorldSnapshot> drainedSnapshots)
    {
        Assert.NotEmpty(drainedSnapshots);
        WorldSnapshot snapshot = drainedSnapshots[0];
        drainedSnapshots.RemoveAt(0);
        memory.ReturnSnapshot(snapshot);
    }

    internal readonly struct FakeClock : IClock
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
