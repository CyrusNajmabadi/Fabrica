using Simulation.Engine;
using Simulation.Memory;
using Simulation.Tests.Helpers;
using Simulation.World;
using Xunit;

namespace Simulation.Tests.Engine;

public sealed class SimulationLoopRunTests
{
    [Fact]
    public void RunOneIteration_UsesElapsedTimeToCrossTheTickThreshold()
    {
        var clockState = new TestClockState { NowNanoseconds = 0 };
        var waiterState = new TestWaiterState();
        var test = SimulationLoopTestContext.Create(
            clock: new TestRecordingClock(clockState),
            waiter: new TestRecordingWaiter(waiterState),
            waiterState: waiterState);

        test.Accessor.Bootstrap();

        long lastTime = 100;
        var accumulator = SimulationConstants.TickDurationNanoseconds - 200;
        clockState.NowNanoseconds = 300;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(300, lastTime);
        Assert.Equal(1, test.Accessor.CurrentTick);
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
        var test = SimulationLoopTestContext.Create(
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
        Assert.Equal(0, test.Accessor.CurrentTick);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(1)],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_ClampsNegativeClockDeltaToZero()
    {
        var clockState = new TestClockState { NowNanoseconds = 50 };
        var waiterState = new TestWaiterState();
        var test = SimulationLoopTestContext.Create(
            clock: new TestRecordingClock(clockState),
            waiter: new TestRecordingWaiter(waiterState),
            waiterState: waiterState);

        test.Accessor.Bootstrap();

        long lastTime = 100;
        long accumulator = 123;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(50, lastTime);
        Assert.Equal(123, accumulator);
        Assert.Equal(0, test.Accessor.CurrentTick);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(1)],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void Run_BootstrapsAndThrowsWhenCancelledDuringTheFirstIdleWait()
    {
        var clockState = new TestClockState { NowNanoseconds = 0 };
        var waiterState = new TestWaiterState();
        var test = SimulationLoopTestContext.Create(
            clock: new TestRecordingClock(clockState),
            waiter: new TestRecordingWaiter(waiterState),
            waiterState: waiterState);

        using var cancellationSource = new CancellationTokenSource();
        waiterState.BeforeWait = cancellationSource.Cancel;

        Assert.Throws<OperationCanceledException>(() => test.Loop.Run(cancellationSource.Token));

        var snapshot = Assert.IsType<WorldSnapshot>(test.Accessor.CurrentSnapshot);
        Assert.Equal(0, test.Accessor.CurrentTick);
        Assert.Same(snapshot, test.Accessor.OldestSnapshot);
        Assert.Same(snapshot, test.Shared.LatestSnapshot);
        Assert.Equal(0, snapshot.TickNumber);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(1)],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void Run_WhenAlreadyCancelled_ThrowsWithoutBootstrapping()
    {
        var clockState = new TestClockState { NowNanoseconds = 0 };
        var waiterState = new TestWaiterState();
        var test = SimulationLoopTestContext.Create(
            clock: new TestRecordingClock(clockState),
            waiter: new TestRecordingWaiter(waiterState),
            waiterState: waiterState);

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        Assert.Throws<OperationCanceledException>(() => test.Loop.Run(cancellationSource.Token));

        Assert.Null(test.Accessor.CurrentSnapshot);
        Assert.Null(test.Accessor.OldestSnapshot);
        Assert.Null(test.Shared.LatestSnapshot);
        Assert.Equal(0, test.Accessor.CurrentTick);
        Assert.Empty(test.WaiterState.WaitCalls);
    }

    private static class SimulationLoopTestContext
    {
        public static SimulationLoopTestContext<TClock, TWaiter> Create<TClock, TWaiter>(
            TClock clock,
            TWaiter waiter,
            TestWaiterState waiterState,
            int poolSize = 8)
            where TClock : struct, IClock
            where TWaiter : struct, IWaiter
        {
            var memory = new MemorySystem(poolSize);
            var shared = new SharedState();
            var loop = new SimulationLoop<TClock, TWaiter>(memory, shared, new SimulationCoordinator(1), clock, waiter);
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
            TestWaiterState waiterState,
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

        public TestWaiterState WaiterState { get; }

        public SimulationLoop<TClock, TWaiter> Loop { get; }

        public SimulationLoop<TClock, TWaiter>.TestAccessor Accessor { get; }
    }
}
