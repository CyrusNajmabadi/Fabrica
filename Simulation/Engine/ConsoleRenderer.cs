using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Temporary production renderer used until real rendering is implemented.
/// </summary>
internal readonly struct ConsoleRenderer : IRenderer
{
    public void Render(WorldSnapshot snapshot)
    {
        // TODO: actual rendering and interpolation.
        Console.WriteLine($"[Render] tick={snapshot.Image.TickNumber}");
    }
}
