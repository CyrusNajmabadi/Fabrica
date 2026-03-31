using Engine.Pipeline;
using Engine.Rendering;
using Engine.Threading;
using Engine.World;

namespace Engine.Tests.Helpers;

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
internal readonly struct TestRecordingClock : IClock
{
    private readonly TestClockState _state;

    public TestRecordingClock(TestClockState state) => _state = state;

    public long NowNanoseconds => _state.NowNanoseconds;
}

/// <summary>
/// Clock that always reads zero. Useful for tests that drive time via the
/// accumulator or <see cref="ProductionLoop{TPayload,TProducer,TClock,TWaiter}.TestAccessor.Tick"/>
/// rather than wall-clock deltas.
/// </summary>
internal readonly struct TestFakeClock : IClock
{
    public long NowNanoseconds => 0;
}

/// <summary>
/// Mutable state shared between a test and its <see cref="TestRecordingWaiter"/>.
/// Captures every Wait call and exposes two optional hooks:
/// <see cref="BeforeWait"/> (parameterless, for simple cancellation triggers) and
/// <see cref="OnWait"/> (receives the duration, for duration-aware assertions).
/// </summary>
internal sealed class TestWaiterState
{
    public readonly List<TimeSpan> WaitCalls = [];

    public Action? BeforeWait { get; set; }
    public Action<TimeSpan>? OnWait { get; set; }
}

/// <summary>
/// Waiter that records every call and invokes optional hooks from
/// <see cref="TestWaiterState"/> before checking cancellation.
/// </summary>
internal readonly struct TestRecordingWaiter : IWaiter
{
    private readonly TestWaiterState _state;

    public TestRecordingWaiter(TestWaiterState state) => _state = state;

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
    public void Consume(ChainNode<WorldImage>? previous, ChainNode<WorldImage> latest, long frameStartNanoseconds, CancellationToken cancellationToken) { }
}
