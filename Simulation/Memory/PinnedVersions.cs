using System.Collections.Concurrent;

namespace Simulation.Memory;

/// <summary>
/// Thread-safe set of snapshot tick numbers that external systems (save tasks, etc.)
/// have explicitly pinned.
///
/// The simulation thread calls <see cref="IsPinned"/> during cleanup.
/// Save and other threads call <see cref="Pin"/> / <see cref="Unpin"/>.
///
/// Conservative race: if a pin is set after the simulation reads it, the snapshot is kept
/// alive one extra cleanup pass — never freed prematurely.
/// </summary>
internal sealed class PinnedVersions
{
    private readonly ConcurrentDictionary<int, byte> _pinned = new();

    public void Pin(int tick)      => _pinned.TryAdd(tick, 0);
    public void Unpin(int tick)    => _pinned.TryRemove(tick, out _);
    public bool IsPinned(int tick) => _pinned.ContainsKey(tick);
}
