using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Engine.Pipeline;

/// <summary>
/// Thread-safe multi-owner registry of snapshot sequence numbers that must not be reclaimed by the production thread. This is the
/// authoritative documentation for the pinning protocol; other types reference this doc.
///
/// WHY THIS EXISTS
///   The production thread (sole memory owner) normally reclaims a node once ConsumptionEpoch has advanced past it. A deferred
///   consumer task, however, holds a live reference to the payload for the duration of its async work — potentially long after the
///   consumption thread has moved on. PinnedVersions lets the consumption thread declare a hold on a sequence before dispatching
///   the deferred task, and the task releases it when done.
///
/// PINNING PROTOCOL
///   Three threads participate; the ordering between their operations is what makes the system safe:
///
///   CONSUMPTION THREAD (single-threaded — no concurrency within this thread):
///     1. Pin(sequenceNumber, consumer) — before dispatching the deferred task.
///     2. Dispatch the Task via ConsumeAsync.
///     3. Advance ConsumptionEpoch — only after pinning is complete.
///
///   Because steps 1 and 3 are sequential on the same thread, the Pin is always visible before the epoch advances past that
///   sequence. The production thread therefore cannot see an advanced epoch for a sequence that hasn't been pinned yet.
///
///   THREADPOOL (deferred consumer task — may run on any thread):
///     4. Task completes (success or fault).
///     5. Consumption thread calls Unpin(sequenceNumber, consumer) on the next frame's DrainCompletedTasks pass.
///
///   PRODUCTION THREAD (during CleanupStaleNodes):
///     6. Walks the chain from _oldest forward, freeing nodes with sequence &lt; ConsumptionEpoch.
///     7. For each candidate, checks IsPinned. If pinned, splices the node into a deferred queue instead of freeing it.
///     8. On subsequent cleanup passes, re-checks the deferred queue and frees nodes whose pins have been released.
///
///   SAFETY ARGUMENT: The production thread only frees sequence &lt; ConsumptionEpoch. The consumption thread always pins
///   *before* advancing the epoch past that sequence (steps 1→3 are ordered on a single thread). Therefore, by the time the
///   production thread's epoch read makes a sequence eligible for cleanup, the pin is already visible in the ConcurrentDictionary.
///   A stale (lower) epoch read is conservative — it delays cleanup, never causes premature freeing.
///
/// WHY ConcurrentDictionary
///   Pin is called on the consumption thread. Unpin is called on the threadpool task thread. IsPinned is called on the production
///   thread. All three can be in flight concurrently. This is the ONLY place in the codebase that requires a concurrent data
///   structure — everything else is confined to a single owning thread.
///
/// PERFORMANCE — WHY THIS DOES NOT IMPACT THE HOT PATH
///   Despite using a concurrent collection, the pinning system has negligible impact on both the production and consumption
///   threads:
///
///   PRODUCTION THREAD (calls IsPinned every tick during CleanupStaleNodes):
///     IsPinned delegates to ConcurrentDictionary.TryGetValue, which is lock-free on .NET 5+ (volatile reads only, no
///     synchronization). For the overwhelming majority of ticks, no deferred consumers are in flight and the dictionary is empty —
///     TryGetValue returns false immediately with no contention. Even when entries exist, the read never blocks and never competes
///     with writers for a lock.
///
///   CONSUMPTION THREAD (DrainCompletedTasks every frame):
///     The per-frame cost is a scan of the in-flight task array (typically 1-2 entries). Task.IsCompleted is a simple volatile read
///     of an internal status field — no allocation, no lock, no contention. Only when a task has actually completed does the loop
///     call Unpin, which happens at most once per deferred consumer per scheduling cycle (e.g. once every 5 minutes for a save
///     consumer). Similarly, MaybeRunConsumers peeks the min-heap (O(1)), and since the PriorityQueue is only ever accessed from
///     the consumption thread, the peek has no concurrency overhead — no volatile reads, no memory barriers. Pin is only called
///     when a consumer is actually due.
///
///   Pin/Unpin (rare — deferred consumer boundaries only):
///     These use ConcurrentDictionary's fine-grained per-bucket locking for writes. At most a handful of entries exist at any
///     given moment (one per in-flight deferred consumer). The only scenario where Pin or Unpin could briefly delay the production
///     thread's IsPinned read is if they happen to hash to the same bucket — but since reads are lock-free on .NET 5+, this
///     cannot actually occur. Pin and Unpin can only contend with each other, and since they run on different threads (consumption
///     vs threadpool) at very low frequency, contention is negligible in practice.
///
/// MULTI-OWNER DESIGN
///   Each sequence maps to a set of <see cref="IPinOwner"/> objects rather than a boolean flag. Currently each deferred consumer
///   acts as its own pin owner, but the multi-owner structure supports future callers — e.g. an external viewer that wants to
///   freeze a specific node for inspection while a deferred consumer is also processing the same sequence.
/// </summary>
internal sealed class PinnedVersions
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<IPinOwner, byte>> _pinned = new();

    public void Pin(int sequenceNumber, IPinOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var owners = _pinned.GetOrAdd(
            sequenceNumber,
            static _ => new ConcurrentDictionary<IPinOwner, byte>(ReferenceOwnerComparer.Instance));

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
