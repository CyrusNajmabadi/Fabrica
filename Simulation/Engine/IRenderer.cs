namespace Simulation.Engine;

/// <summary>
/// Renders one frame of output.
/// Separated from the consumption loop so tests can observe render sequencing
/// without depending on console output.
/// </summary>
internal interface IRenderer
{
    /// <summary>
    /// Called by the consumption loop once per frame (≈60 Hz).
    ///
    /// <paramref name="frame"/> bundles the simulation snapshots (previous and
    /// current) together with an interpolation factor and non-simulation engine
    /// state.  See <see cref="RenderFrame"/> for the full contract.
    ///
    /// SNAPSHOT LIFETIME — CRITICAL:
    ///   The snapshots referenced by <see cref="RenderFrame.Previous"/> and
    ///   <see cref="RenderFrame.Current"/> are ONLY valid for the duration of
    ///   this call.  Implementations MUST NOT store, cache, or hand off these
    ///   references.  The consumption loop owns epoch management and may rotate
    ///   or release the snapshots immediately after this method returns.
    ///
    /// INTERPOLATION:
    ///   Under normal operation, <see cref="RenderFrame.Previous"/> and
    ///   <see cref="RenderFrame.Current"/> are two distinct simulation states.
    ///   <see cref="RenderFrame.Interpolation"/> provides raw integral timing
    ///   data; the renderer computes the blend factor (typically
    ///   elapsed / tickDuration, clamped to [0, 1], where 0 = Previous and
    ///   1 = Current).  On the very first frame, Previous is null — the
    ///   renderer should display Current as-is.
    /// </summary>
    void Render(in RenderFrame frame);
}
