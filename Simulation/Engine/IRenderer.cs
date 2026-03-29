using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Renders the latest published world snapshot.
/// Separated from the consumption loop so tests can observe render sequencing
/// without depending on console output.
/// </summary>
internal interface IRenderer
{
    /// <summary>
    /// Called by the consumption loop once per frame (≈60 Hz).
    ///
    /// <paramref name="previous"/> is the snapshot that was passed as
    /// <paramref name="current"/> on the immediately preceding successful call, or
    /// <c>null</c> on the very first frame.
    ///
    /// <paramref name="current"/> is the latest snapshot published by the simulation.
    ///
    /// IMPORTANT — same-snapshot calls:
    ///   When the simulation has not advanced since the last render,
    ///   <paramref name="previous"/> and <paramref name="current"/> will be the
    ///   same object reference.  The consumption loop always calls Render regardless;
    ///   this lets the renderer drive sub-tick visual effects, animations, or
    ///   interpolation even when the underlying world state has not changed.
    ///   Implementations that have nothing to do in this case can detect it cheaply
    ///   with a reference comparison (<c>ReferenceEquals(previous, current)</c>).
    /// </summary>
    void Render(WorldSnapshot? previous, WorldSnapshot current);
}
