using Engine;
using Engine.Memory;
using Engine.Pipeline;
using Engine.Simulation;
using Engine.Tests.Helpers;
using Engine.Threading;
using Engine.World;
using Xunit;

using ChainNode = Engine.Pipeline.BaseProductionLoop<Engine.World.WorldImage>.ChainNode;
using NodeAllocator = Engine.Pipeline.BaseProductionLoop<Engine.World.WorldImage>.NodeAllocator;

namespace Engine.Tests;

public sealed class SimulationLoopRunTests
{
    [Fact]
    public void RunOneIteration_UsesElapsedTimeToCrossTheTickThreshold()
    {
        var clockState = new TestClockState { NowNanoseconds = 0 };
        var waiterState = new TestWaiterState();
        var test = ProductionLoopTestContext.Create(
            clock: new TestRecordingClock(clockState),
            waiter: new TestRecordingWaiter(waiterState),
            waiterState: waiterState);

        test.Accessor.Bootstrap();

        long lastTime = 100;
        var accumulator = SimulationConstants.TickDurationNanoseconds - 200;
        clockState.NowNanoseconds = 300;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(300, lastTime);
        Assert.Equal(1, test.Accessor.CurrentSequence);
        Assert.Equal(0, accumulator);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(1)],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_UpdatesLastTimeEvenWhenCancelledDuringIdleWait()
    {
        var clockState = new TestClockState { NowNanoseconds = 250 };
        var waiterState = new TestWaiterState();
        var test = ProductionLoopTestContext.Create(
            clock: new TestRecordingClock(clockState),
            waiter: new TestRecordingWaiter(waiterState),
            waiterState: waiterState);

        test.Accessor.Bootstrap();

        long lastTime = 100;
        long accumulator = 0;

        using var cancellationSource = new CancellationTokenSource();
        waiterState.BeforeWait = cancellationSource.Cancel;

        Assert.Throws<OperationCanceledException>(
            () => test.Accessor.RunOneIteration(cancellationSource.Token, ref lastTime, ref accumulator));

        Assert.Equal(250, lastTime);
        Assert.Equal(150, accumulator);
        Assert.Equal(0, test.Accessor.CurrentSequence);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(1)],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_ClampsNegativeClockDeltaToZero()
    {
        var clockState = new TestClockState { NowNanoseconds = 50 };
        var waiterState = new TestWaiterState();
        var test = ProductionLoopTestContext.Create(
            clock: new TestRecordingClock(clockState),
            waiter: new TestRecordingWaiter(waiterState),
            waiterState: waiterState);

        test.Accessor.Bootstrap();

        long lastTime = 100;
        long accumulator = 123;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(50, lastTime);
        Assert.Equal(123, accumulator);
        Assert.Equal(0, test.Accessor.CurrentSequence);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(1)],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void Run_BootstrapsAndThrowsWhenCancelledDuringTheFirstIdleWait()
    {
        var clockState = new TestClockState { NowNanoseconds = 0 };
        var waiterState = new TestWaiterState();
        var test = ProductionLoopTestContext.Create(
            clock: new TestRecordingClock(clockState),
            waiter: new TestRecordingWaiter(waiterState),
            waiterState: waiterState);

        using var cancellationSource = new CancellationTokenSource();
        waiterState.BeforeWait = cancellationSource.Cancel;

        Assert.Throws<OperationCanceledException>(() => test.Loop.Run(cancellationSource.Token));

        var node = test.Accessor.CurrentNode!;
        Assert.Equal(0, test.Accessor.CurrentSequence);
        Assert.Same(node, test.Accessor.OldestNode);
        Assert.Same(node, test.Shared.LatestNode);
        Assert.Equal(0, node.SequenceNumber);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(1)],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void Run_WhenAlreadyCancelled_ThrowsWithoutBootstrapping()
    {
        var clockState = new TestClockState { NowNanoseconds = 0 };
        var waiterState = new TestWaiterState();
        var test = ProductionLoopTestContext.Create(
            clock: new TestRecordingClock(clockState),
            waiter: new TestRecordingWaiter(waiterState),
            waiterState: waiterState);

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        Assert.Throws<OperationCanceledException>(() => test.Loop.Run(cancellationSource.Token));

        Assert.Null(test.Accessor.CurrentNode);
        Assert.Null(test.Accessor.OldestNode);
        Assert.Null(test.Shared.LatestNode);
        Assert.Equal(0, test.Accessor.CurrentSequence);
        Assert.Empty(test.WaiterState.WaitCalls);
    }

    private static class ProductionLoopTestContext
    {
        public static ProductionLoopTestContext<TClock, TWaiter> Create<TClock, TWaiter>(
            TClock clock,
            TWaiter waiter,
            TestWaiterState waiterState,
            int poolSize = 8)
            where TClock : struct, IClock
            where TWaiter : struct, IWaiter
        {
            var nodePool = new ObjectPool<ChainNode, NodeAllocator>(poolSize);
            var imagePool = new ObjectPool<WorldImage, WorldImageAllocator>(poolSize);
            var pinnedVersions = new PinnedVersions();
            var shared = new SharedState<WorldImage>();
            var producer = new SimulationProducer(imagePool, new SimulationCoordinator(1));
            var loop = new ProductionLoop<WorldImage, SimulationProducer, TClock, TWaiter>(
                nodePool, pinnedVersions, shared, producer, clock, waiter);
            return new ProductionLoopTestContext<TClock, TWaiter>(shared, waiterState, loop);
        }
    }

    private sealed class ProductionLoopTestContext<TClock, TWaiter>
        where TClock : struct, IClock
        where TWaiter : struct, IWaiter
    {
        internal ProductionLoopTestContext(
            SharedState<WorldImage> shared,
            TestWaiterState waiterState,
            ProductionLoop<WorldImage, SimulationProducer, TClock, TWaiter> loop)
        {
            this.Shared = shared;
            this.WaiterState = waiterState;
            this.Loop = loop;
            this.Accessor = loop.GetTestAccessor();
        }

        public SharedState<WorldImage> Shared { get; }

        public TestWaiterState WaiterState { get; }

        public ProductionLoop<WorldImage, SimulationProducer, TClock, TWaiter> Loop { get; }

        public ProductionLoop<WorldImage, SimulationProducer, TClock, TWaiter>.TestAccessor Accessor { get; }
    }
}
