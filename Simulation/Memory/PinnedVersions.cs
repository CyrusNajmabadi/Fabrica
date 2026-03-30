using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Simulation.Memory;

/// <summary>
/// Thread-safe multi-owner registry of snapshot tick numbers that must not be
/// reclaimed by the simulation.
///
/// WHY THIS EXISTS
///   The simulation thread (sole memory owner) normally reclaims a snapshot once
///   ConsumptionEpoch has advanced past it.  A save task, however, holds a live
///   reference to the WorldImage for the full duration of the save — potentially
///   long after the consumption thread has moved on.  PinnedVersions lets the
///   consumption thread declare a hold on a tick before dispatching the save task,
///   and the save task releases it when done.
///
/// WHY ConcurrentDictionary IS REQUIRED HERE
///   Pin is called on the consumption thread (before dispatch).
///   Unpin is called on the threadpool save task thread (after save completes).
///   IsPinned is called on the simulation thread (during cleanup).
///   All three can be in flight concurrently, so the data structure must be
///   safe for concurrent access.
///   This is the ONLY place in the codebase that requires concurrent data
///   structures — everything else is confined to a single owning thread.
///
/// WHY IT IS STILL LIGHTWEIGHT
///   Pin/Unpin are called only at save boundaries — once every SaveIntervalTicks
///   (≈5 minutes of game time at 40 ticks/sec).  At most one entry is present
///   in the dictionary at any given moment.  IsPinned is called once per snapshot
///   during CleanupStaleSnapshots, but for the overwhelming majority of snapshots
///   the dictionary is empty and TryGetValue returns false immediately.
///
/// CONSERVATIVE RACE FOR IsPinned
///   If the simulation reads IsPinned = false and then Pin arrives on the
///   consumption thread a moment later, the simulation might free the snapshot.
///   This cannot happen in practice: Pin is always called before
///   ConsumptionEpoch advances past the tick (see ConsumptionLoop.MaybeStartSave),
///   and the simulation only frees tick &lt; ConsumptionEpoch.  The epoch protects
///   the snapshot until after Pin is guaranteed to be visible.
///
/// MULTI-OWNER DESIGN
///   Each tick maps to a set of owner objects rather than a boolean flag.
///   Currently only one owner (the save system's _savePinOwner) is used, but the
///   multi-owner structure exists to support future callers — e.g. an external
///   viewer that wants to freeze a specific snapshot for inspection while a save
///   is also in flight for the same tick.
/// </summary>
internal sealed class PinnedVersions
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<object, byte>> _pinned = new();

    public void Pin(int tick, object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var owners = _pinned.GetOrAdd(
            tick,
            _ => new ConcurrentDictionary<object, byte>(ReferenceOwnerComparer.Instance));

        if (!owners.TryAdd(owner, 0))
            throw new InvalidOperationException("The same owner pinned the same tick more than once.");
    }

    public void Unpin(int tick, object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (!_pinned.TryGetValue(tick, out var owners))
            throw new InvalidOperationException("Attempted to unpin a tick that is not currently pinned.");

        if (!owners.TryRemove(owner, out _))
            throw new InvalidOperationException("Attempted to unpin a tick for an owner that is not currently pinned.");

        if (owners.IsEmpty)
            _pinned.TryRemove(new KeyValuePair<int, ConcurrentDictionary<object, byte>>(tick, owners));
    }

    public bool IsPinned(int tick) =>
        _pinned.TryGetValue(tick, out var owners) && !owners.IsEmpty;

    private sealed class ReferenceOwnerComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceOwnerComparer Instance = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
