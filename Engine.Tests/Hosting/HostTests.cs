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
    public void Run_CallsShutdownOnProducerAndConsumer_AfterLoopsExit()
    {
        var tracker = new ShutdownTracker();
        var producer = new TestTrackingProducer(tracker);
        var consumer = new TestTrackingConsumer(tracker);

        var nodePool = new ObjectPool<ChainNode, ChainNodeAllocator>(16);
        var shared = new SharedPipelineState<WorldImage>();

        var productionLoop = new ProductionLoop<WorldImage, TestTrackingProducer, TestFakeClock, TestSilentWaiter>(
            nodePool, shared, producer, default, default);
        var consumptionLoop = new ConsumptionLoop<WorldImage, TestTrackingConsumer, TestFakeClock, TestSilentWaiter>(
            shared, consumer, default, default, []);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var host = new Host<WorldImage, TestTrackingProducer, TestTrackingConsumer, TestFakeClock, TestSilentWaiter>(
            productionLoop, consumptionLoop);

        host.Run(cts.Token);

        Assert.True(tracker.ProducerShutdownCalled, "Host.Run must call Shutdown on the production loop after it exits.");
        Assert.True(tracker.ConsumerShutdownCalled, "Host.Run must call Shutdown on the consumption loop after it exits.");
    }

    // ── Shared tracking state ─────────────────────────────────────────────

    private sealed class ShutdownTracker
    {
        private bool _producerShutdownCalled;
        private bool _consumerShutdownCalled;

        public bool ProducerShutdownCalled { get => _producerShutdownCalled; set => _producerShutdownCalled = value; }
        public bool ConsumerShutdownCalled { get => _consumerShutdownCalled; set => _consumerShutdownCalled = value; }
    }

    // ── Test doubles ──────────────────────────────────────────────────────

    /// <summary>
    /// Waiter that returns immediately without throwing, allowing loops to exit
    /// cleanly via their cancellation-token while-loop check.
    /// </summary>
    private readonly struct TestSilentWaiter : IWaiter
    {
        public void Wait(TimeSpan duration, CancellationToken cancellationToken) { }
    }

    private readonly struct TestFakeClock : IClock
    {
        public long NowNanoseconds => 0;
    }

    private readonly struct TestTrackingProducer(ShutdownTracker tracker) : IProducer<WorldImage>
    {
        private readonly ShutdownTracker _tracker = tracker;

        public readonly WorldImage CreateInitialPayload(CancellationToken cancellationToken) =>
            default(WorldImage.Allocator).Allocate();

        public readonly WorldImage Produce(WorldImage current, CancellationToken cancellationToken) =>
            default(WorldImage.Allocator).Allocate();

        public readonly void ReleaseResources(WorldImage payload) { }

        public readonly void Shutdown() =>
            _tracker.ProducerShutdownCalled = true;
    }

    private readonly struct TestTrackingConsumer(ShutdownTracker tracker) : IConsumer<WorldImage>
    {
        private readonly ShutdownTracker _tracker = tracker;

        public readonly void Consume(ChainNode previous, ChainNode latest, long frameStartNanoseconds, CancellationToken cancellationToken) { }

        public readonly void Shutdown() =>
            _tracker.ConsumerShutdownCalled = true;
    }
}
