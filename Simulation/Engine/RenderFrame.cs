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
///   <see cref="Previous"/> and <see cref="Latest"/> are owned by the
///   consumption loop, which manages the epoch to keep them alive for the
///   duration of the Render call.  Implementations MUST NOT store, cache,
///   or pass these references beyond the return of <see cref="IRenderer.Render"/>.
///   After the call returns the consumption loop may rotate or release them.
///
/// CHAIN ACCESS
///   When the simulation publishes multiple ticks between render frames,
///   <see cref="Previous"/> and <see cref="Latest"/> may be several ticks apart.
///   The full forward-linked chain from Previous to Latest is guaranteed alive
///   during the Render call — the renderer can walk <c>Previous.Next</c>
///   repeatedly to visit every intermediate snapshot if desired.
///
/// INTERPOLATION MODEL
///   The consumption loop holds two distinct snapshot references and rotates
///   them when the simulation publishes new data: the old Latest becomes
///   Previous, and the new snapshot becomes Latest.  Between rotations the
///   pair is stable, giving the renderer two real simulation endpoints to
///   blend between — no extrapolation needed.
///
///   <see cref="Interpolation"/> provides raw integral timing data so the
///   renderer can compute a blend factor (typically elapsed / tickDuration,
///   clamped to [0, 1]).
///
///   On the very first frame (before two distinct snapshots exist),
///   <see cref="Previous"/> is null.  The renderer should display Latest as-is.
/// </summary>
internal readonly struct RenderFrame
{
    public required WorldSnapshot? Previous { get; init; }
    public required WorldSnapshot Latest { get; init; }
    public required InterpolationClock Interpolation { get; init; }
    public required EngineStatus EngineStatus { get; init; }
}

/// <summary>
/// Raw integral timing data for interpolation between
/// <see cref="RenderFrame.Previous"/> and <see cref="RenderFrame.Latest"/>.
///
/// The standard blend factor is:
///   <c>Math.Clamp((double)ElapsedNanoseconds / TickDurationNanoseconds, 0, 1)</c>
///
/// where 0 = show Previous and 1 = show Latest.  The renderer is free to
/// apply clamping, easing, or any other policy.
/// </summary>
internal readonly struct InterpolationClock
{
    /// <summary>
    /// Wall-clock nanoseconds elapsed since <see cref="RenderFrame.Latest"/>
    /// was published by the simulation thread.
    /// </summary>
    public required long ElapsedNanoseconds { get; init; }

    /// <summary>
    /// Duration of one simulation tick in nanoseconds.
    /// </summary>
    public required long TickDurationNanoseconds { get; init; }
}
