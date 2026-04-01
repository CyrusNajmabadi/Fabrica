using Fabrica.Pipeline.Memory;
using Fabrica.Pipeline.Threading;

namespace Fabrica.Pipeline.Tests.Helpers;

using ChainNode = BaseProductionLoop<TestPayload>.ChainNode;
using ChainNodeAllocator = BaseProductionLoop<TestPayload>.ChainNode.Allocator;

/// <summary>
/// Pipeline timing for tests — self-contained constants that mirror a typical simulation configuration without depending on
/// any engine-layer type.
/// </summary>
internal static class TestPipelineConfiguration
{
    public const long TickDurationNanoseconds = 25_000_000L;
    public const long RenderIntervalNanoseconds = 16_666_666L;

    public static PipelineConfiguration Default => new()
    {
        TickDurationNanoseconds = 25_000_000L,
        IdleYieldNanoseconds = 1_000_000L,
        PressureLowWaterMarkNanoseconds = 100_000_000L,
        PressureHardCeilingNanoseconds = 2_000_000_000L,
        PressureBucketCount = 8,
        PressureBaseDelayNanoseconds = 1_000_000L,
        PressureMaxDelayNanoseconds = 64_000_000L,
        RenderIntervalNanoseconds = 16_666_666L,
    };
}

internal sealed class TestClockState
{
    public long NowNanoseconds { get; set; }
}

internal readonly struct TestRecordingClock(TestClockState state) : IClock
{
    private readonly TestClockState _state = state;

    public long NowNanoseconds => _state.NowNanoseconds;
}

internal readonly struct TestFakeClock : IClock
{
    public long NowNanoseconds => 0;
}

internal sealed class TestWaiterState
{
    public readonly List<TimeSpan> WaitCalls = [];

    public Action? BeforeWait { get; set; }
    public Action<TimeSpan>? OnWait { get; set; }
}

internal readonly struct TestRecordingWaiter(TestWaiterState state) : IWaiter
{
    private readonly TestWaiterState _state = state;

    public void Wait(TimeSpan duration, CancellationToken cancellationToken)
    {
        _state.WaitCalls.Add(duration);
        _state.BeforeWait?.Invoke();
        _state.OnWait?.Invoke(duration);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

internal readonly struct TestNoOpWaiter : IWaiter
{
    public void Wait(TimeSpan duration, CancellationToken cancellationToken) =>
        cancellationToken.ThrowIfCancellationRequested();
}

internal readonly struct TestNoOpConsumer : IConsumer<TestPayload>
{
    public void Consume(
        ChainNode previous,
        ChainNode latest,
        long frameStartNanoseconds,
        CancellationToken cancellationToken)
    { }
}

/// <summary>
/// Minimal concrete subclass of <see cref="BaseProductionLoop{TPayload}"/> for tests that need to create and mutate chain
/// nodes outside of a full production loop.
/// </summary>
internal sealed class TestChainHarness : BaseProductionLoop<TestPayload>
{
    public TestChainHarness(int poolSize = 16)
        : base(new ObjectPool<ChainNode, ChainNodeAllocator>(poolSize), new PinnedVersions())
    {
    }

    public TestChainHarness(
        ObjectPool<ChainNode, ChainNodeAllocator> nodePool,
        PinnedVersions pinnedVersions)
        : base(nodePool, pinnedVersions)
    {
    }

    protected override void ReleasePayloadResources(TestPayload payload) { }
}
