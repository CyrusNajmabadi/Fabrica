using Fabrica.Core.Memory;
using Fabrica.Core.Threading;
using Fabrica.Pipeline.Tests.Helpers;
using Xunit;

namespace Fabrica.Pipeline.Tests.Pipeline;

using ChainNode = BaseProductionLoop<TestPayload>.ChainNode;
using ChainNodeAllocator = BaseProductionLoop<TestPayload>.ChainNode.Allocator;

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
        var shared = new SharedPipelineState<TestPayload>();

        var loop = new ProductionLoop<TestPayload, CountingProducer, TestFakeClock, TestSilentWaiter>(
            nodePool, shared, producer, default, default, TestPipelineConfiguration.Default);
        var accessor = loop.GetTestAccessor();

        accessor.Bootstrap();

        using var cancellationTokenSource = new CancellationTokenSource();
        producerState.CancellationTokenSource = cancellationTokenSource;
        producerState.CancelAfterTickCount = CancelAfterTick;

        long lastTime = 0;
        var accumulator = TestPipelineConfiguration.TickDurationNanoseconds * TicksQueued;

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

    private readonly struct CountingProducer(CountingProducerState state) : IProducer<TestPayload>
    {
        private readonly CountingProducerState _state = state;

        public readonly TestPayload CreateInitialPayload(CancellationToken cancellationToken) =>
            default(TestPayload.Allocator).Allocate();

        public readonly TestPayload Produce(TestPayload current, CancellationToken cancellationToken)
        {
            _state.TickCount++;
            if (_state.TickCount >= _state.CancelAfterTickCount)
                _state.CancellationTokenSource?.Cancel();
            return default(TestPayload.Allocator).Allocate();
        }

        public readonly void ReleaseResources(TestPayload payload) { }
    }
}
