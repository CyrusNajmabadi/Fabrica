using Fabrica.Engine;
using Fabrica.Engine.World;
using Fabrica.Pipeline;
using Fabrica.Pipeline.Memory;
using Fabrica.Pipeline.Threading;
using Fabrica.Tests.Helpers;
using Xunit;

namespace Fabrica.Tests.Engine;

using ChainNode = BaseProductionLoop<WorldImage>.ChainNode;
using ChainNodeAllocator = BaseProductionLoop<WorldImage>.ChainNode.Allocator;

public sealed class ProductionLoopCancellationTests
{
    /// <summary>
    /// Demonstrates the bug: when many ticks are queued in the accumulator and cancellation fires mid-batch,
    /// ProcessAvailableTicks processes all remaining ticks instead of exiting promptly.
    /// </summary>
    [Fact]
    public void ProcessAvailableTicks_StopsPromptly_WhenCancelledMidBatch()
    {
        const int TicksQueued = 20;
        const int CancelAfterTick = 3;

        var producerState = new CountingProducerState();
        var producer = new CountingProducer(producerState);

        var nodePool = new ObjectPool<ChainNode, ChainNodeAllocator>(TicksQueued + 4);
        var shared = new SharedPipelineState<WorldImage>();

        var loop = new ProductionLoop<WorldImage, CountingProducer, TestFakeClock, TestSilentWaiter>(
            nodePool, shared, producer, default, default, TestPipelineConfiguration.Default);
        var accessor = loop.GetTestAccessor();

        accessor.Bootstrap();

        using var cancellationTokenSource = new CancellationTokenSource();
        producerState.CancellationTokenSource = cancellationTokenSource;
        producerState.CancelAfterTickCount = CancelAfterTick;

        long lastTime = 0;
        var accumulator = SimulationConstants.TickDurationNanoseconds * TicksQueued;

        Assert.Throws<OperationCanceledException>(
            () => accessor.RunOneIteration(cancellationTokenSource.Token, ref lastTime, ref accumulator));

        Assert.True(
            producerState.TickCount <= CancelAfterTick + 1,
            $"Expected at most {CancelAfterTick + 1} ticks but got {producerState.TickCount}. " +
            "ProcessAvailableTicks should check cancellation between ticks.");
    }

    // ── Test doubles ──────────────────────────────────────────────────────

    /// <summary>
    /// Waiter that returns immediately without throwing, so the only cancellation check is the one we're testing inside
    /// ProcessAvailableTicks.
    /// </summary>
    private readonly struct TestSilentWaiter : IWaiter
    {
        public void Wait(TimeSpan duration, CancellationToken cancellationToken) { }
    }

    private sealed class CountingProducerState
    {
        private int _tickCount;
        private int _cancelAfterTickCount;
        private CancellationTokenSource? _cancellationTokenSource;

        public int TickCount { get => _tickCount; set => _tickCount = value; }
        public int CancelAfterTickCount { get => _cancelAfterTickCount; set => _cancelAfterTickCount = value; }
        public CancellationTokenSource? CancellationTokenSource { get => _cancellationTokenSource; set => _cancellationTokenSource = value; }
    }

    private readonly struct CountingProducer(CountingProducerState state) : IProducer<WorldImage>
    {
        private readonly CountingProducerState _state = state;

        public readonly WorldImage CreateInitialPayload(CancellationToken cancellationToken) =>
            default(WorldImage.Allocator).Allocate();

        public readonly WorldImage Produce(WorldImage current, CancellationToken cancellationToken)
        {
            _state.TickCount++;
            if (_state.TickCount >= _state.CancelAfterTickCount)
                _state.CancellationTokenSource?.Cancel();
            return default(WorldImage.Allocator).Allocate();
        }

        public readonly void ReleaseResources(WorldImage payload) { }
    }
}
