using Engine.Hosting;
using Engine.Memory;
using Engine.Pipeline;
using Engine.Threading;
using Engine.World;
using Xunit;

namespace Engine.Tests.Hosting;

using ChainNode = BaseProductionLoop<WorldImage>.ChainNode;
using ChainNodeAllocator = BaseProductionLoop<WorldImage>.ChainNode.Allocator;

public sealed class HostTests
{
    [Fact]
    public void Run_PropagatesProducerException()
    {
        var producer = new TestThrowingProducer(new TickCounter(), throwOnTickNumber: 2);
        var consumer = new TestNoOpConsumer();
        var clock = new TestAutoAdvancingClock(new AutoAdvancingClockState());

        var nodePool = new ObjectPool<ChainNode, ChainNodeAllocator>(16);
        var shared = new SharedPipelineState<WorldImage>();

        var productionLoop = new ProductionLoop<WorldImage, TestThrowingProducer, TestAutoAdvancingClock, TestSilentWaiter>(
            nodePool, shared, producer, clock, default);
        var consumptionLoop = new ConsumptionLoop<WorldImage, TestNoOpConsumer, TestAutoAdvancingClock, TestSilentWaiter>(
            shared, consumer, clock, default, []);

        var host = new Host<WorldImage, TestThrowingProducer, TestNoOpConsumer, TestAutoAdvancingClock, TestSilentWaiter>(
            productionLoop, consumptionLoop);

        var ex = Assert.Throws<InvalidOperationException>(() => host.Run(CancellationToken.None));
        Assert.Equal("Deliberate producer fault.", ex.Message);
    }

    /// <summary>
    /// Deterministic repro for the unhandled-OCE crash.  Synchronization gates
    /// guarantee the consumption thread is parked inside <c>Wait</c> (blocking
    /// on the cancellation token's WaitHandle) before the production thread
    /// throws.  When the fault cancels the linked CTS, the consumption thread
    /// wakes and throws <see cref="OperationCanceledException"/>.
    /// With <c>catch (Exception ex) when (ex is not OperationCanceledException)</c>,
    /// OCE is not caught and crashes the process.
    /// </summary>
    [Fact]
    public void Run_PropagatesProducerException_WhenConsumptionThrowsOCE()
    {
        var synchronization = new ThreadSynchronizationState();
        var producer = new TestSynchronizedThrowingProducer(new TickCounter(), synchronization, throwOnTickNumber: 2);
        var consumer = new TestNoOpConsumer();
        var clock = new TestAutoAdvancingClock(new AutoAdvancingClockState());

        var nodePool = new ObjectPool<ChainNode, ChainNodeAllocator>(16);
        var shared = new SharedPipelineState<WorldImage>();

        var productionLoop = new ProductionLoop<WorldImage, TestSynchronizedThrowingProducer, TestAutoAdvancingClock, TestSynchronizedWaiter>(
            nodePool, shared, producer, clock, new TestSynchronizedWaiter(synchronization));
        var consumptionLoop = new ConsumptionLoop<WorldImage, TestNoOpConsumer, TestAutoAdvancingClock, TestSynchronizedWaiter>(
            shared, consumer, clock, new TestSynchronizedWaiter(synchronization), []);

        var host = new Host<WorldImage, TestSynchronizedThrowingProducer, TestNoOpConsumer, TestAutoAdvancingClock, TestSynchronizedWaiter>(
            productionLoop, consumptionLoop);

        var ex = Assert.Throws<InvalidOperationException>(() => host.Run(CancellationToken.None));
        Assert.Equal("Deliberate producer fault.", ex.Message);
    }

    // ── Test doubles ────────────────────────────────────────────────────

    private readonly struct TestSilentWaiter : IWaiter
    {
        public void Wait(TimeSpan duration, CancellationToken cancellationToken) { }
    }

    private sealed class AutoAdvancingClockState
    {
        private long _now;
        public long Read() => Interlocked.Add(ref _now, SimulationConstants.TickDurationNanoseconds);
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

    private readonly struct TestThrowingProducer(TickCounter counter, int throwOnTickNumber) : IProducer<WorldImage>
    {
        private readonly TickCounter _counter = counter;
        private readonly int _throwOnTickNumber = throwOnTickNumber;

        public readonly WorldImage CreateInitialPayload(CancellationToken cancellationToken) =>
            default(WorldImage.Allocator).Allocate();

        public readonly WorldImage Produce(WorldImage current, CancellationToken cancellationToken)
        {
            if (++_counter.Value >= _throwOnTickNumber)
                throw new InvalidOperationException("Deliberate producer fault.");
            return default(WorldImage.Allocator).Allocate();
        }

        public readonly void ReleaseResources(WorldImage payload) { }
    }

    private sealed class ThreadSynchronizationState
    {
        public readonly ManualResetEventSlim ConsumptionParked = new(false);
        public readonly ManualResetEventSlim ProducerMayThrow = new(false);
    }

    /// <summary>
    /// Production thread: no-op.
    /// Consumption thread: signals <c>ConsumptionParked</c>, then blocks on the
    /// cancellation token's WaitHandle.  This guarantees the consumption thread
    /// is inside <c>Wait</c> before the production thread throws.
    /// </summary>
    private readonly struct TestSynchronizedWaiter(ThreadSynchronizationState synchronization) : IWaiter
    {
        private readonly ThreadSynchronizationState _synchronization = synchronization;

        public readonly void Wait(TimeSpan duration, CancellationToken cancellationToken)
        {
            if (Thread.CurrentThread.Name != "Consumption")
                return;

            _synchronization.ConsumptionParked.Set();
            cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Waits for the consumption thread to park before throwing, ensuring the
    /// consumption thread is deterministically inside <c>Wait</c> when the
    /// fault triggers cancellation.
    /// </summary>
    private readonly struct TestSynchronizedThrowingProducer(
        TickCounter counter, ThreadSynchronizationState synchronization, int throwOnTickNumber) : IProducer<WorldImage>
    {
        private readonly TickCounter _counter = counter;
        private readonly ThreadSynchronizationState _synchronization = synchronization;
        private readonly int _throwOnTickNumber = throwOnTickNumber;

        public readonly WorldImage CreateInitialPayload(CancellationToken cancellationToken) =>
            default(WorldImage.Allocator).Allocate();

        public readonly WorldImage Produce(WorldImage current, CancellationToken cancellationToken)
        {
            if (++_counter.Value >= _throwOnTickNumber)
            {
                _synchronization.ConsumptionParked.Wait();
                throw new InvalidOperationException("Deliberate producer fault.");
            }

            return default(WorldImage.Allocator).Allocate();
        }

        public readonly void ReleaseResources(WorldImage payload) { }
    }

    private readonly struct TestNoOpConsumer : IConsumer<WorldImage>
    {
        public readonly void Consume(ChainNode previous, ChainNode latest, long frameStartNanoseconds, CancellationToken cancellationToken) { }
    }
}
