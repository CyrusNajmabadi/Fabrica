using Fabrica.Engine;
using Fabrica.Engine.Rendering;
using Fabrica.Engine.World;
using Fabrica.Pipeline;
using Fabrica.Pipeline.Memory;
using Fabrica.Pipeline.Threading;

namespace Fabrica.Tests.Helpers;

using ChainNode = BaseProductionLoop<WorldImage>.ChainNode;
using ChainNodeAllocator = BaseProductionLoop<WorldImage>.ChainNode.Allocator;

/// <summary>Pipeline timing for tests — mirrors <see cref="SimulationConstants"/>.</summary>
internal static class TestPipelineConfiguration
{
    public static PipelineConfiguration Default => new()
    {
        TickDurationNanoseconds = SimulationConstants.TickDurationNanoseconds,
        IdleYieldNanoseconds = SimulationConstants.IdleYieldNanoseconds,
        PressureLowWaterMarkNanoseconds = SimulationConstants.PressureLowWaterMarkNanoseconds,
        PressureHardCeilingNanoseconds = SimulationConstants.PressureHardCeilingNanoseconds,
        PressureBucketCount = SimulationConstants.PressureBucketCount,
        PressureBaseDelayNanoseconds = SimulationConstants.PressureBaseDelayNanoseconds,
        PressureMaxDelayNanoseconds = SimulationConstants.PressureMaxDelayNanoseconds,
        RenderIntervalNanoseconds = SimulationConstants.RenderIntervalNanoseconds,
    };
}

/// <summary>
/// Mutable clock state shared between a test and its <see cref="TestRecordingClock"/>.
/// </summary>
internal sealed class TestClockState
{
    public long NowNanoseconds { get; set; }
}

/// <summary>
/// Clock backed by a mutable <see cref="TestClockState"/> so tests can control time.
/// </summary>
internal readonly struct TestRecordingClock(TestClockState state) : IClock
{
    private readonly TestClockState _state = state;

    public long NowNanoseconds => _state.NowNanoseconds;
}

/// <summary>
/// Clock that always reads zero. Useful for tests that drive time via the accumulator or
/// <see cref="ProductionLoop{TPayload,TProducer,TClock,TWaiter}.TestAccessor.Tick"/> rather than wall-clock deltas.
/// </summary>
internal readonly struct TestFakeClock : IClock
{
    public long NowNanoseconds => 0;
}

/// <summary>
/// Mutable state shared between a test and its <see cref="TestRecordingWaiter"/>. Captures every Wait call and exposes two
/// optional hooks: <see cref="BeforeWait"/> (parameterless, for simple cancellation triggers) and <see cref="OnWait"/> (receives
/// the duration, for duration-aware assertions).
/// </summary>
internal sealed class TestWaiterState
{
    public readonly List<TimeSpan> WaitCalls = [];

    public Action? BeforeWait { get; set; }
    public Action<TimeSpan>? OnWait { get; set; }
}

/// <summary>
/// Waiter that records every call and invokes optional hooks from <see cref="TestWaiterState"/> before checking cancellation.
/// </summary>
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

/// <summary>
/// Waiter that honors cancellation but does not sleep or record calls.
/// </summary>
internal readonly struct TestNoOpWaiter : IWaiter
{
    public void Wait(TimeSpan duration, CancellationToken cancellationToken) =>
        cancellationToken.ThrowIfCancellationRequested();
}

internal readonly struct TestNoOpRenderer : IRenderer
{
    public void Render(in RenderFrame frame) { }
}

internal readonly struct TestNoOpConsumer : IConsumer<WorldImage>
{
    public void Consume(ChainNode previous, ChainNode latest, long frameStartNanoseconds, CancellationToken cancellationToken) { }
}

/// <summary>
/// Minimal concrete subclass of <see cref="BaseProductionLoop{TPayload}"/> for tests that need to create and mutate chain nodes
/// outside of a full production loop.
/// </summary>
internal sealed class TestChainHarness : BaseProductionLoop<WorldImage>
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

    protected override void ReleasePayloadResources(WorldImage payload) { }
}
