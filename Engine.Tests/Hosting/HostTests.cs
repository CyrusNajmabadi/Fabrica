using Engine.Hosting;
using Engine.Memory;
using Engine.Pipeline;
using Engine.Tests.Helpers;
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

        var nodePool = new ObjectPool<ChainNode, ChainNodeAllocator>(8);
        var imagePool = new ObjectPool<WorldImage, WorldImage.Allocator>(8);
        var shared = new SharedPipelineState<WorldImage>();

        var producer = new TestTrackingProducer(imagePool, tracker);
        var consumer = new TestTrackingConsumer(tracker);

        var host = new Host<WorldImage, TestTrackingProducer, TestTrackingConsumer, TestFakeClock, TestSilentWaiter>(
            new ProductionLoop<WorldImage, TestTrackingProducer, TestFakeClock, TestSilentWaiter>(
                nodePool, shared, producer, new TestFakeClock(), new TestSilentWaiter()),
            new ConsumptionLoop<WorldImage, TestTrackingConsumer, TestFakeClock, TestSilentWaiter>(
                shared, consumer, new TestFakeClock(), new TestSilentWaiter(), []));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        host.Run(cts.Token);

        Assert.True(tracker.ProducerShutdownCalled, "Expected producer Shutdown to be called after Host.Run returns.");
        Assert.True(tracker.ConsumerShutdownCalled, "Expected consumer Shutdown to be called after Host.Run returns.");
    }

    // ── Test doubles ──────────────────────────────────────────────────────

    /// <summary>
    /// Waiter that returns immediately without checking cancellation, so the
    /// loops exit via their <c>while (!IsCancellationRequested)</c> check
    /// instead of throwing <see cref="OperationCanceledException"/>.
    /// </summary>
    private readonly struct TestSilentWaiter : IWaiter
    {
        public void Wait(TimeSpan duration, CancellationToken cancellationToken) { }
    }

    private sealed class ShutdownTracker
    {
        private volatile bool _producerShutdownCalled;
        private volatile bool _consumerShutdownCalled;

        public bool ProducerShutdownCalled
        {
            get => _producerShutdownCalled;
            set => _producerShutdownCalled = value;
        }

        public bool ConsumerShutdownCalled
        {
            get => _consumerShutdownCalled;
            set => _consumerShutdownCalled = value;
        }
    }

    private readonly struct TestTrackingProducer(ObjectPool<WorldImage, WorldImage.Allocator> imagePool, ShutdownTracker tracker) : IProducer<WorldImage>
    {
        private readonly ObjectPool<WorldImage, WorldImage.Allocator> _imagePool = imagePool;
        private readonly ShutdownTracker _tracker = tracker;

        public WorldImage CreateInitialPayload(CancellationToken cancellationToken) =>
            _imagePool.Rent();

        public WorldImage Produce(WorldImage current, CancellationToken cancellationToken) =>
            _imagePool.Rent();

        public void ReleaseResources(WorldImage payload) =>
            _imagePool.Return(payload);

        public void Shutdown() =>
            _tracker.ProducerShutdownCalled = true;
    }

    private readonly struct TestTrackingConsumer(ShutdownTracker tracker) : IConsumer<WorldImage>
    {
        private readonly ShutdownTracker _tracker = tracker;

        public void Consume(ChainNode previous, ChainNode latest, long frameStartNanoseconds, CancellationToken cancellationToken) { }

        public void Shutdown() =>
            _tracker.ConsumerShutdownCalled = true;
    }
}
