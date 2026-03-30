using Simulation.Engine;
using Simulation.World;

namespace Simulation.Tests.Helpers;

/// <summary>
/// Mutable clock state shared between a test and its <see cref="RecordingClock"/>.
/// </summary>
internal sealed class ClockState
{
    public long NowNanoseconds { get; set; }
}

/// <summary>
/// Clock backed by a mutable <see cref="ClockState"/> so tests can control time.
/// </summary>
internal readonly struct RecordingClock : IClock
{
    private readonly ClockState _state;

    public RecordingClock(ClockState state) => _state = state;

    public long NowNanoseconds => _state.NowNanoseconds;
}

/// <summary>
/// Clock that always reads zero. Useful for tests that drive time via the
/// accumulator or <see cref="SimulationLoop{TClock,TWaiter}.TestAccessor.Tick"/>
/// rather than wall-clock deltas.
/// </summary>
internal readonly struct FakeClock : IClock
{
    public long NowNanoseconds => 0;
}

/// <summary>
/// Mutable state shared between a test and its <see cref="RecordingWaiter"/>.
/// Captures every Wait call and exposes two optional hooks:
/// <see cref="BeforeWait"/> (parameterless, for simple cancellation triggers) and
/// <see cref="OnWait"/> (receives the duration, for duration-aware assertions).
/// </summary>
internal sealed class WaiterState
{
    public readonly List<TimeSpan> WaitCalls = [];

    public Action? BeforeWait { get; set; }
    public Action<TimeSpan>? OnWait { get; set; }
}

/// <summary>
/// Waiter that records every call and invokes optional hooks from
/// <see cref="WaiterState"/> before checking cancellation.
/// </summary>
internal readonly struct RecordingWaiter : IWaiter
{
    private readonly WaiterState _state;

    public RecordingWaiter(WaiterState state) => _state = state;

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
internal readonly struct NoWaiter : IWaiter
{
    public void Wait(TimeSpan duration, CancellationToken cancellationToken) =>
        cancellationToken.ThrowIfCancellationRequested();
}

internal readonly struct NoOpSaveRunner : ISaveRunner
{
    public void RunSave(WorldImage image, int tick, Action<WorldImage, int> saveAction) { }
}

internal readonly struct NoOpSaver : ISaver
{
    public void Save(WorldImage image, int tick) { }
}

internal readonly struct NoOpRenderer : IRenderer
{
    public void Render(in RenderFrame frame) { }
}

internal readonly record struct SaveInvocation(WorldImage Image, int Tick, Action<WorldImage, int> SaveAction);
