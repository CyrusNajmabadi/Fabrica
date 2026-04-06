using Fabrica.Engine.World;

namespace Fabrica.Engine.Rendering;

/// <summary>
/// Everything the renderer needs for one frame: simulation snapshots, interpolation timing, and non-simulation engine state.
///
/// Bundling these into a single struct keeps the <see cref="IRenderer.Render"/> signature stable as new data is added — callers
/// never need a parade of additional parameters.
///
/// SNAPSHOT LIFETIME
///   <see cref="Previous"/> and <see cref="Latest"/> are valid only for the duration of the <see cref="IRenderer.Render"/> call.
///   The consumption loop advances past earlier entries immediately after, so the production thread may clean up their payloads.
///   Implementations MUST NOT store, cache, or pass these references beyond the return of <see cref="IRenderer.Render"/>.
///
/// INTERPOLATION MODEL
///   The consumption loop holds back one entry between frames so the renderer always has two distinct simulation states to
///   interpolate between. <see cref="Previous"/> is the held-back entry from the prior frame; <see cref="Latest"/> is the most
///   recently published entry. Between frames the pair is stable.
///
///   <see cref="Interpolation"/> provides raw integral timing data so the renderer can compute a blend factor (typically
///   elapsed / tickDuration, clamped to [0, 1], where 0 = Previous and 1 = Latest).
///
///   INVARIANT: <see cref="Previous"/> and <see cref="Latest"/> are always two distinct simulation states. The consumption loop
///   waits until at least two entries are available before calling Consume, so both are always valid.
/// </summary>
public readonly struct RenderFrame
{
    public required WorldImage Previous { get; init; }
    public required WorldImage Latest { get; init; }
    public required InterpolationClock Interpolation { get; init; }
    public required EngineStatus EngineStatus { get; init; }
}

/// <summary>
/// Raw integral timing data for interpolation between <see cref="RenderFrame.Previous"/> and <see cref="RenderFrame.Latest"/>.
///
/// The standard blend factor is:
///   <c>Math.Clamp((double)ElapsedNanoseconds / TickDurationNanoseconds, 0, 1)</c>
///
/// where 0 = show Previous and 1 = show Latest. The renderer is free to apply clamping, easing, or any other policy.
/// </summary>
public readonly struct InterpolationClock
{
    /// <summary>
    /// Wall-clock nanoseconds elapsed since <see cref="RenderFrame.Latest"/> was published by the simulation thread.
    /// </summary>
    public required long ElapsedNanoseconds { get; init; }

    /// <summary>
    /// Duration of one simulation tick in nanoseconds.
    /// </summary>
    public required long TickDurationNanoseconds { get; init; }
}
