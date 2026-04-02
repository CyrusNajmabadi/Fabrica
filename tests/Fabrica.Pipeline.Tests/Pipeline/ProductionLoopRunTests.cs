using Fabrica.Core.Collections;
using Fabrica.Core.Memory;
using Fabrica.Core.Threading;
using Fabrica.Pipeline.Tests.Helpers;
using Xunit;

namespace Fabrica.Pipeline.Tests.Pipeline;

public sealed class ProductionLoopRunTests
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
        var accumulator = TestPipelineConfiguration.TickDurationNanoseconds - 200;
        clockState.NowNanoseconds = 300;

        test.Accessor.RunOneIteration(CancellationToken.None, ref lastTime, ref accumulator);

        Assert.Equal(300, lastTime);
        Assert.Equal(2, test.Shared.Queue.ProducerPosition);
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
        Assert.Equal(1, test.Shared.Queue.ProducerPosition);
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
        Assert.Equal(1, test.Shared.Queue.ProducerPosition);
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

        Assert.NotNull(test.Accessor.CurrentPayload);
        Assert.Equal(1, test.Shared.Queue.ProducerPosition);
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

        Assert.Null(test.Accessor.CurrentPayload);
        Assert.Equal(0, test.Shared.Queue.ProducerPosition);
        Assert.Empty(test.WaiterState.WaitCalls);
    }

    [Fact]
    public void Run_ExitsNormally_WhenWaiterCancelsWithoutThrowing()
    {
        using var cts = new CancellationTokenSource();

        var queue = new ProducerConsumerQueue<PipelineEntry<TestPayload>>();
        var shared = new SharedPipelineState<TestPayload>(queue);

        var loop = new ProductionLoop<TestPayload, TestSimpleProducer, TestFakeClock, CancelWithoutThrowWaiter>(
            shared, new TestSimpleProducer(), default, new CancelWithoutThrowWaiter(cts), TestPipelineConfiguration.Default);

        loop.Run(cts.Token);

        Assert.True(shared.Queue.ProducerPosition >= 1, "Bootstrap should have appended at least one entry.");
    }

    // ── Waiter that cancels the CTS but does not throw, so Run exits via the while condition ──

    private readonly struct CancelWithoutThrowWaiter(CancellationTokenSource cts) : IWaiter
    {
        private readonly CancellationTokenSource _cts = cts;

        public void Wait(TimeSpan duration, CancellationToken cancellationToken)
            => _cts.Cancel();
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
            => ProductionLoopTestContext<TClock, TWaiter>.Create(clock, waiter, waiterState, poolSize);
    }

    private sealed class ProductionLoopTestContext<TClock, TWaiter>
        where TClock : struct, IClock
        where TWaiter : struct, IWaiter
    {
        public static ProductionLoopTestContext<TClock, TWaiter> Create(
            TClock clock,
            TWaiter waiter,
            TestWaiterState waiterState,
            int poolSize = 8)
        {
            var queue = new ProducerConsumerQueue<PipelineEntry<TestPayload>>();
            var payloadPool = new ObjectPool<TestPayload, TestPayload.Allocator>(poolSize);
            var shared = new SharedPipelineState<TestPayload>(queue);
            var producer = new TestWorkerProducer(payloadPool, 1);
            var loop = new ProductionLoop<TestPayload, TestWorkerProducer, TClock, TWaiter>(
                shared, producer, clock, waiter, TestPipelineConfiguration.Default);
            return new ProductionLoopTestContext<TClock, TWaiter>(shared, waiterState, loop);
        }

        private ProductionLoopTestContext(
            SharedPipelineState<TestPayload> shared,
            TestWaiterState waiterState,
            ProductionLoop<TestPayload, TestWorkerProducer, TClock, TWaiter> loop)
        {
            this.Shared = shared;
            this.WaiterState = waiterState;
            this.Loop = loop;
            this.Accessor = loop.GetTestAccessor();
        }

        public SharedPipelineState<TestPayload> Shared { get; }

        public TestWaiterState WaiterState { get; }

        public ProductionLoop<TestPayload, TestWorkerProducer, TClock, TWaiter> Loop { get; }

        public ProductionLoop<TestPayload, TestWorkerProducer, TClock, TWaiter>.TestAccessor Accessor { get; }
    }
}
