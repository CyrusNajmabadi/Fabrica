using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Temporary production saver used until real serialization is implemented.
/// </summary>
internal readonly struct ConsoleSaver : ISaver<WorldSnapshot>
{
    public void Save(WorldSnapshot node, int sequenceNumber)
    {
        // TODO: actual serialisation.
        Console.WriteLine($"[Save]   tick={sequenceNumber} — saving...");
        Thread.Sleep(1000); // placeholder for real I/O
        Console.WriteLine($"[Save]   tick={sequenceNumber} — done.");
    }
}
