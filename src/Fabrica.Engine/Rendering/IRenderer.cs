namespace Fabrica.Engine.Rendering;

/// <summary>
/// Renders one frame of output. Separated from the consumption loop so tests can observe render sequencing without depending on
/// console output.
/// </summary>
public interface IRenderer
{
    /// <summary>
    /// Called by the consumption loop once per frame (≈60 Hz).
    ///
    /// <paramref name="frame"/> bundles the simulation snapshots (previous and latest) together with interpolation timing and
    /// non-simulation engine state. See <see cref="RenderFrame"/> for the full contract.
    ///
    /// SNAPSHOT LIFETIME — CRITICAL:
    ///   The snapshots referenced by <see cref="RenderFrame.Previous"/> and <see cref="RenderFrame.Latest"/> are ONLY valid for
    ///   the duration of this call. Implementations MUST NOT store, cache, or hand off these references. The consumption loop
    ///   advances past earlier entries immediately after this method returns, and the production thread may reclaim their payloads.
    ///
    /// INTERPOLATION:
    ///   Under normal operation, <see cref="RenderFrame.Previous"/> and
    ///   <see cref="RenderFrame.Latest"/> are two distinct simulation states.
    ///   <see cref="RenderFrame.Interpolation"/> provides raw integral timing
    ///   data; the renderer computes the blend factor (typically
    ///   elapsed / tickDuration, clamped to [0, 1], where 0 = Previous and
    ///   1 = Latest).  The consumption loop waits for at least two entries
    ///   before calling Consume, so Previous and Latest are always distinct.
    /// </summary>
    void Render(in RenderFrame frame);
}
