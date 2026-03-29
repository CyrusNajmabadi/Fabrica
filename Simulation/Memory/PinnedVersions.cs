using System.Collections.Concurrent;

namespace Simulation.Memory;

/// <summary>
/// Thread-safe set of snapshot tick numbers that external systems (save tasks, etc.)
/// have explicitly pinned.  The simulation thread checks this before freeing a snapshot.
///
/// Conservative race: if a pin is set after the simulation reads it, the snapshot is
/// kept alive one extra cleanup pass — never freed prematurely.
/// </summary>
internal sealed class PinnedVersions
{
    private readonly ConcurrentDictionary<int, byte> _pinned = new();

    public void Pin(int tick)   => _pinned.TryAdd(tick, 0);
    public void Unpin(int tick) => _pinned.TryRemove(tick, out _);
    public bool IsPinned(int tick) => _pinned.ContainsKey(tick);

    /// <summary>
    /// Oldest pinned tick, or int.MaxValue if nothing is pinned.
    /// Called on the simulation thread during cleanup — O(n) over a typically tiny set.
    /// </summary>
    public int MinPinned
    {
        get
        {
            int min = int.MaxValue;
            foreach (int key in _pinned.Keys)
                if (key < min) min = key;
            return min;
        }
    }
}
