using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

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
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<object, byte>> _pinned = new();

    public void Pin(int tick, object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        ConcurrentDictionary<object, byte> owners = _pinned.GetOrAdd(
            tick,
            _ => new ConcurrentDictionary<object, byte>(ReferenceOwnerComparer.Instance));

        if (!owners.TryAdd(owner, 0))
            throw new InvalidOperationException("The same owner pinned the same tick more than once.");
    }

    public void Unpin(int tick, object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (!_pinned.TryGetValue(tick, out ConcurrentDictionary<object, byte>? owners))
            throw new InvalidOperationException("Attempted to unpin a tick that is not currently pinned.");

        if (!owners.TryRemove(owner, out _))
            throw new InvalidOperationException("Attempted to unpin a tick for an owner that is not currently pinned.");

        if (owners.IsEmpty)
            _pinned.TryRemove(new KeyValuePair<int, ConcurrentDictionary<object, byte>>(tick, owners));
    }

    public bool IsPinned(int tick) =>
        _pinned.TryGetValue(tick, out ConcurrentDictionary<object, byte>? owners) && !owners.IsEmpty;

    private sealed class ReferenceOwnerComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceOwnerComparer Instance = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
