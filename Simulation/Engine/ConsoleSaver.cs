using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Temporary production saver used until real serialization is implemented.
/// </summary>
internal readonly struct ConsoleSaver : ISaver
{
    public void Save(WorldImage image, int tick)
    {
        // TODO: actual serialisation.
        Console.WriteLine($"[Save]   tick={tick} — saving...");
        Thread.Sleep(1000); // placeholder for real I/O
        Console.WriteLine($"[Save]   tick={tick} — done.");
    }
}
