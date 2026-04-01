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

    private readonly struct TestNoOpConsumer : IConsumer<WorldImage>
    {
        public readonly void Consume(ChainNode previous, ChainNode latest, long frameStartNanoseconds, CancellationToken cancellationToken) { }
    }
}
