using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Everything the renderer needs for one frame: simulation snapshots,
/// interpolation timing, and non-simulation engine state.
///
/// Bundling these into a single struct keeps the <see cref="IRenderer.Render"/>
/// signature stable as new data is added — callers never need a parade of
/// additional parameters.
///
/// SNAPSHOT LIFETIME
///   <see cref="Previous"/> and <see cref="Current"/> are owned by the
///   consumption loop, which manages the epoch to keep them alive for the
///   duration of the Render call.  Implementations MUST NOT store, cache,
///   or pass these references beyond the return of <see cref="IRenderer.Render"/>.
///   After the call returns the consumption loop may rotate or release them.
///
/// INTERPOLATION MODEL ("one tick behind")
///   The consumption loop always holds two distinct simulation snapshots and
///   interpolates between them.  When the simulation publishes a new tick,
///   the old Current becomes Previous and the new tick becomes Current.
///   <see cref="Interpolation"/> provides raw integral timing data so the
///   renderer can compute a blend factor (typically elapsed / tickDuration,
///   clamped to [0, 1]).
///
///   On the very first frame (before two distinct snapshots exist),
///   <see cref="Previous"/> is null.  The renderer should display Current as-is.
/// </summary>
internal readonly struct RenderFrame
{
    public required WorldSnapshot? Previous { get; init; }
    public required WorldSnapshot Current { get; init; }
    public required InterpolationClock Interpolation { get; init; }
    public required EngineStatus EngineStatus { get; init; }
}

/// <summary>
/// Raw integral timing data for interpolation between
/// <see cref="RenderFrame.Previous"/> and <see cref="RenderFrame.Current"/>.
///
/// The standard blend factor is:
///   <c>Math.Clamp((double)ElapsedNanoseconds / TickDurationNanoseconds, 0, 1)</c>
///
/// where 0 = show Previous and 1 = show Current.  The renderer is free to
/// apply clamping, easing, or any other policy.
/// </summary>
internal readonly struct InterpolationClock
{
    /// <summary>
    /// Wall-clock nanoseconds elapsed since <see cref="RenderFrame.Current"/>
    /// was published by the simulation thread.
    /// </summary>
    public required long ElapsedNanoseconds { get; init; }

    /// <summary>
    /// Duration of one simulation tick in nanoseconds.
    /// </summary>
    public required long TickDurationNanoseconds { get; init; }
}
