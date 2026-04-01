using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Engine.Pipeline;

/// <summary>
/// Thread-safe multi-owner registry of snapshot sequence numbers that must not be reclaimed by the simulation.
///
/// WHY THIS EXISTS
///   The simulation thread (sole memory owner) normally reclaims a node once ConsumptionEpoch has advanced past it. A deferred
///   consumer task, however, holds a live reference to the payload for the full duration of its async work — potentially long
///   after the consumption thread has moved on. PinnedVersions lets the consumption thread declare a hold on a sequence before
///   dispatching the deferred task, and the task releases it when done.
///
/// WHY ConcurrentDictionary IS REQUIRED HERE
///   Pin is called on the consumption thread (before dispatch).
///   Unpin is called on the threadpool task thread (after work completes).
///   IsPinned is called on the simulation thread (during cleanup).
///   All three can be in flight concurrently, so the data structure must be
///   safe for concurrent access.
///   This is the ONLY place in the codebase that requires concurrent data
///   structures — everything else is confined to a single owning thread.
///
/// WHY IT IS STILL LIGHTWEIGHT
///   Pin/Unpin are called only at deferred-consumer boundaries — infrequent relative to the per-tick hot path. At most a handful
///   of entries are present at any given moment. IsPinned is called once per node during CleanupStaleNodes, but for the
///   overwhelming majority of nodes the dictionary is empty and TryGetValue returns false immediately.
///
/// CONSERVATIVE RACE FOR IsPinned
///   If the simulation reads IsPinned = false and then Pin arrives on the
///   consumption thread a moment later, the simulation might free the node.
///   This cannot happen in practice: Pin is always called before
///   ConsumptionEpoch advances past the sequence (see ConsumptionLoop),
///   and the simulation only frees sequence &lt; ConsumptionEpoch.  The epoch
///   protects the node until after Pin is guaranteed to be visible.
///
/// MULTI-OWNER DESIGN
///   Each sequence maps to a set of <see cref="IPinOwner"/> objects rather than a boolean flag. Currently each deferred consumer
///   acts as its own pin owner, but the multi-owner structure exists to support future callers — e.g. an external viewer that
///   wants to freeze a specific node for inspection while a deferred consumer is also processing the same sequence.
/// </summary>
internal sealed class PinnedVersions
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<IPinOwner, byte>> _pinned = new();

    public void Pin(int sequenceNumber, IPinOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var owners = _pinned.GetOrAdd(
            sequenceNumber,
            _ => new ConcurrentDictionary<IPinOwner, byte>(ReferenceOwnerComparer.Instance));

        if (!owners.TryAdd(owner, 0))
            throw new InvalidOperationException("The same owner pinned the same sequence more than once.");
    }

    public void Unpin(int sequenceNumber, IPinOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (!_pinned.TryGetValue(sequenceNumber, out var owners))
            throw new InvalidOperationException("Attempted to unpin a sequence that is not currently pinned.");

        if (!owners.TryRemove(owner, out _))
            throw new InvalidOperationException("Attempted to unpin a sequence for an owner that is not currently pinned.");

        if (owners.IsEmpty)
            _pinned.TryRemove(new KeyValuePair<int, ConcurrentDictionary<IPinOwner, byte>>(sequenceNumber, owners));
    }

    public bool IsPinned(int sequenceNumber) =>
        _pinned.TryGetValue(sequenceNumber, out var owners) && !owners.IsEmpty;

    private sealed class ReferenceOwnerComparer : IEqualityComparer<IPinOwner>
    {
        public static readonly ReferenceOwnerComparer Instance = new();

        public bool Equals(IPinOwner? x, IPinOwner? y) => ReferenceEquals(x, y);

        public int GetHashCode(IPinOwner pinOwner) => RuntimeHelpers.GetHashCode(pinOwner);
    }
}
