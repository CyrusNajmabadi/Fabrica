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
    /// current) together with non-simulation engine state (save status, future
    /// diagnostics, etc.).  See <see cref="RenderFrame"/> for details.
    ///
    /// IMPORTANT — same-snapshot calls:
    ///   When the simulation has not advanced since the last render,
    ///   <see cref="RenderFrame.Previous"/> and <see cref="RenderFrame.Current"/>
    ///   will be the same object reference.  The consumption loop always calls
    ///   Render regardless; this lets the renderer drive sub-tick visual effects,
    ///   animations, or interpolation even when the underlying world state has not
    ///   changed.  Implementations that have nothing to do in this case can detect
    ///   it cheaply with <c>ReferenceEquals(frame.Previous, frame.Current)</c>.
    /// </summary>
    void Render(in RenderFrame frame);
}
