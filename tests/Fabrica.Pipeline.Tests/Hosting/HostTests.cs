using Fabrica.Core.Threading;
using Fabrica.Core.Threading.Queues;
using Fabrica.Pipeline.Hosting;
using Fabrica.Pipeline.Tests.Helpers;
using Xunit;

namespace Fabrica.Pipeline.Tests.Hosting;

public sealed class HostTests
{
    [Fact]
    public async Task RunAsync_PropagatesProducerException()
    {
        var producer = new TestThrowingProducer(new TickCounter(), throwOnTickNumber: 2);
        var consumer = new TestNoOpConsumer();
        var clock = new TestAutoAdvancingClock(new AutoAdvancingClockState());

        var queue = new ProducerConsumerQueue<PipelineEntry<TestPayload>>();
        var shared = new SharedPipelineState<TestPayload>(queue);

        var productionLoop = new ProductionLoop<TestPayload, TestThrowingProducer, TestAutoAdvancingClock, TestSilentWaiter>(
            shared, producer, clock, default, TestPipelineConfiguration.Default);
        var consumptionLoop = new ConsumptionLoop<TestPayload, TestNoOpConsumer, TestAutoAdvancingClock, TestSilentWaiter>(
            shared, consumer, clock, default, [], TestPipelineConfiguration.Default);

        var host = new Host<TestPayload, TestThrowingProducer, TestNoOpConsumer, TestAutoAdvancingClock, TestSilentWaiter>(
            productionLoop, consumptionLoop);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunAsync(CancellationToken.None));
        Assert.Equal("Deliberate producer fault.", ex.Message);
    }

    /// <summary>
    /// Deterministic repro for the unhandled-OCE crash (PR #56). With a pre-cancelled token, both loops throw
    /// <see cref="OperationCanceledException"/> immediately. The old <c>when</c> filter rejected OCE, leaving it unhandled and
    /// crashing the process. With the <see cref="TaskCompletionSource"/>-based design, cancellation flows through as a standard
    /// <see cref="TaskCanceledException"/>.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithPreCancelledToken_ThrowsTaskCanceledException()
    {
        var producer = new TestThrowingProducer(new TickCounter(), throwOnTickNumber: int.MaxValue);
        var consumer = new TestNoOpConsumer();
        var clock = new TestAutoAdvancingClock(new AutoAdvancingClockState());

        var queue = new ProducerConsumerQueue<PipelineEntry<TestPayload>>();
        var shared = new SharedPipelineState<TestPayload>(queue);

        var productionLoop = new ProductionLoop<TestPayload, TestThrowingProducer, TestAutoAdvancingClock, TestSilentWaiter>(
            shared, producer, clock, default, TestPipelineConfiguration.Default);
        var consumptionLoop = new ConsumptionLoop<TestPayload, TestNoOpConsumer, TestAutoAdvancingClock, TestSilentWaiter>(
            shared, consumer, clock, default, [], TestPipelineConfiguration.Default);

        var host = new Host<TestPayload, TestThrowingProducer, TestNoOpConsumer, TestAutoAdvancingClock, TestSilentWaiter>(
            productionLoop, consumptionLoop);

        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() => host.RunAsync(cancellationTokenSource.Token));
    }

    /// <summary>
    /// Both loops exit normally (while-condition false, no exception), so both <see cref="TaskCompletionSource"/> instances call
    /// <see cref="TaskCompletionSource.SetResult"/> and <see cref="Task.WhenAll"/> completes without faulting.
    ///
    /// A <see cref="Barrier"/> synchronises the two loop threads so that cancellation happens while both are inside their waiter
    /// (after all <c>ThrowIfCancellationRequested</c> checks), eliminating the race where one thread sees the cancelled token at
    /// the top of its next iteration.
    /// </summary>
    [Fact]
    public async Task RunAsync_CompletesNormally_WhenBothLoopsExitCleanly()
    {
        using var cts = new CancellationTokenSource();
        var state = new BarrierCancelState(cts);

        var queue = new ProducerConsumerQueue<PipelineEntry<TestPayload>>();
        var shared = new SharedPipelineState<TestPayload>(queue);

        var productionLoop = new ProductionLoop<TestPayload, TestSimpleProducer, TestFakeClock, BarrierCancelWaiter>(
            shared, new TestSimpleProducer(), default, new BarrierCancelWaiter(state), TestPipelineConfiguration.Default);
        var consumptionLoop = new ConsumptionLoop<TestPayload, TestNoOpConsumer, TestFakeClock, BarrierCancelWaiter>(
            shared, new TestNoOpConsumer(), default, new BarrierCancelWaiter(state), [], TestPipelineConfiguration.Default);

        var host = new Host<TestPayload, TestSimpleProducer, TestNoOpConsumer, TestFakeClock, BarrierCancelWaiter>(
            productionLoop, consumptionLoop);

        await host.RunAsync(cts.Token);
    }

    // ── Test doubles ────────────────────────────────────────────────────

    private readonly struct TestSilentWaiter : IWaiter
    {
        public void Wait(TimeSpan duration, CancellationToken cancellationToken) { }
    }

    private sealed class AutoAdvancingClockState
    {
        private long _now;
        public long Read()
            => Interlocked.Add(ref _now, TestPipelineConfiguration.TickDurationNanoseconds);
    }

    private readonly struct TestAutoAdvancingClock(AutoAdvancingClockState state) : IClock
    {
        private readonly AutoAdvancingClockState _state = state;
        public readonly long NowNanoseconds => _state.Read();
    }

    private sealed class TickCounter
    {
        private int _value;
        public int Value { get => _value; set => _value = value; }
    }

    private sealed class BarrierCancelState(CancellationTokenSource cts)
    {
        private readonly Barrier _barrier = new(2);
        private readonly CancellationTokenSource _cts = cts;

        public void OnWait()
        {
            _barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            _cts.Cancel();
        }
    }

    private readonly struct BarrierCancelWaiter(BarrierCancelState state) : IWaiter
    {
        private readonly BarrierCancelState _state = state;

        public void Wait(TimeSpan duration, CancellationToken cancellationToken)
            => _state.OnWait();
    }

    private readonly struct TestThrowingProducer(TickCounter counter, int throwOnTickNumber) : IProducer<TestPayload>
    {
        private readonly TickCounter _counter = counter;
        private readonly int _throwOnTickNumber = throwOnTickNumber;

        public readonly TestPayload CreateInitialPayload(CancellationToken cancellationToken)
            => default(TestPayload.Allocator).Allocate();

        public readonly TestPayload Produce(TestPayload current, CancellationToken cancellationToken)
        {
            if (++_counter.Value >= _throwOnTickNumber)
                throw new InvalidOperationException("Deliberate producer fault.");
            return default(TestPayload.Allocator).Allocate();
        }

        public readonly void ReleaseResources(TestPayload payload) { }
    }
}
