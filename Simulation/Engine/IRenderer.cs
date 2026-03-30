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
    /// latest) together with interpolation timing and non-simulation engine
    /// state.  See <see cref="RenderFrame"/> for the full contract.
    ///
    /// SNAPSHOT LIFETIME — CRITICAL:
    ///   The snapshots referenced by <see cref="RenderFrame.Previous"/> and
    ///   <see cref="RenderFrame.Latest"/> are ONLY valid for the duration of
    ///   this call.  Implementations MUST NOT store, cache, or hand off these
    ///   references.  The consumption loop owns epoch management and may rotate
    ///   or release the snapshots immediately after this method returns.
    ///
    /// CHAIN ACCESS:
    ///   The full forward-linked chain from Previous to Latest is alive during
    ///   this call.  The renderer may walk <c>Previous.Next</c> to visit every
    ///   intermediate snapshot between the two endpoints.  <c>Latest.Next</c>
    ///   MUST NOT be read — the simulation may concurrently link a new node,
    ///   making the pointer unreliable.
    ///
    /// INTERPOLATION:
    ///   Under normal operation, <see cref="RenderFrame.Previous"/> and
    ///   <see cref="RenderFrame.Latest"/> are two distinct simulation states.
    ///   <see cref="RenderFrame.Interpolation"/> provides raw integral timing
    ///   data; the renderer computes the blend factor (typically
    ///   elapsed / tickDuration, clamped to [0, 1], where 0 = Previous and
    ///   1 = Latest).  On the very first frame, Previous is null — the
    ///   renderer should display Latest as-is.
    /// </summary>
    void Render(in RenderFrame frame);
}
