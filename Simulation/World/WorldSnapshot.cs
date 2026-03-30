using System.Diagnostics;

namespace Simulation.World;

/// <summary>
/// A node in the snapshot chain.  Wraps one WorldImage and holds a forward pointer
/// to the next (newer) snapshot.
///
/// CHAIN STRUCTURE
///   The simulation builds a singly-linked forward chain, oldest → newest:
///
///     [tick 0] → [tick 1] → [tick 2] → … → [tick N]
///      _oldest                                _current / LatestSnapshot
///
///   Only the simulation thread reads or writes the chain.  The forward pointer
///   and ref-count are therefore not synchronised — no atomics, no locks needed.
///
///   The forward pointer (<c>_next</c>) is private.  The simulation loop accesses
///   it via <see cref="NextInChain"/> for chain management.  Consumers (renderers)
///   use <see cref="Chain"/> to obtain a safe, bounded struct iterator that stops
///   at a given endpoint — preventing accidental reads past the published frontier.
///
/// REF COUNTING
///   Each snapshot starts with refcount = 1 (set by Initialize).  AddRef and
///   Release exist to support future structural sharing: if two consecutive
///   snapshots point to the same WorldImage subtree node, that node will carry
///   a refcount > 1 and must not be freed until the last snapshot drops its
///   reference.  Currently every snapshot has refcount 1 throughout its lifetime
///   and FreeSnapshot frees it via a single Release() call.
///   All ref-count operations are on the simulation thread — no atomics needed.
///
/// PINNED EXTRACTION AND ClearNext
///   When CleanupStaleSnapshots encounters a pinned snapshot it cannot yet free,
///   it calls ClearNext() before moving the snapshot to _pinnedQueue.  Severing
///   the forward pointer is necessary so that _oldestSnapshot can advance past
///   the pinned node: without it, the chain walk would stop there and all
///   subsequent snapshots would be blocked from reclamation for the duration of
///   the save.
/// </summary>
internal sealed class WorldSnapshot
{
    /// <summary>
    /// The underlying world state for this snapshot.  Contains simulation data
    /// (belt state, machine state, etc.) that the renderer reads during
    /// interpolation.
    /// </summary>
    public WorldImage Image { get; private set; } = null!;

    /// <summary>
    /// Monotonically increasing simulation tick that produced this snapshot.
    /// Tick 0 is the initial state created by Bootstrap.  Each call to
    /// <c>SimulationLoop.Tick</c> increments by 1.
    ///
    /// Used for epoch management, save scheduling, pin identification, and
    /// ordering within the snapshot chain.
    /// </summary>
    public int TickNumber { get; private set; }

    /// <summary>
    /// Wall-clock time (nanoseconds) when the simulation published this snapshot
    /// via the volatile write to LatestSnapshot.  Set on the simulation thread
    /// before the release fence, so it is visible to any thread that reads the
    /// snapshot via the matching acquire fence.
    ///
    /// Used by the consumption loop to compute the interpolation factor between
    /// consecutive snapshots for smooth rendering.  Lives on the snapshot (not
    /// on WorldImage) because it is publication metadata, not world state.
    /// </summary>
    public long PublishTimeNanoseconds { get; private set; }

    private WorldSnapshot? _next;
    private int _refCount;

    /// <summary>
    /// Internal accessor for the simulation loop's chain management (cleanup,
    /// pinned-queue extraction).  Not for use by consumers — use
    /// <see cref="Chain"/> instead.
    /// </summary>
    internal WorldSnapshot? NextInChain => _next;

    /// <summary>
    /// Prepares this snapshot for use after being rented from the pool.
    /// Sets the image, tick number, clears the forward pointer, and initialises
    /// the ref count to 1.
    ///
    /// Called by the simulation thread via <see cref="Memory.MemorySystem.RentSnapshot"/>.
    /// </summary>
    internal void Initialize(WorldImage image, int tickNumber)
    {
        Debug.Assert(_refCount == 0, "Initialize called on a snapshot still in use");
        this.Image = image;
        this.TickNumber = tickNumber;
        this.PublishTimeNanoseconds = 0;
        _next = null;
        _refCount = 1;
    }

    /// <summary>
    /// Records the wall-clock publish time.  Called by the simulation thread
    /// immediately before the volatile write to LatestSnapshot, so the
    /// release/acquire pair makes the value visible to consumers.
    /// </summary>
    internal void MarkPublished(long timeNanoseconds) => this.PublishTimeNanoseconds = timeNanoseconds;

    /// <summary>Links the next snapshot in the chain. Called once per snapshot by the simulation thread.</summary>
    internal void SetNext(WorldSnapshot next)
    {
        Debug.Assert(_next is null, "SetNext called more than once on the same snapshot");
        _next = next;
    }

    /// <summary>
    /// Severs the forward pointer when this snapshot is extracted from the live chain
    /// into the pinned queue, so that the snapshots that followed it can be freed normally.
    /// </summary>
    internal void ClearNext() => _next = null;

    internal void AddRef()
    {
        Debug.Assert(_refCount > 0, "AddRef on a zero-refcount (freed) snapshot");
        _refCount++;
    }

    /// <summary>
    /// Decrements the ref count.  When it reaches zero the caller must return this
    /// snapshot to the pool.  Nulls out Image and forward pointer so pooled instances
    /// do not keep objects alive unnecessarily.
    /// </summary>
    internal void Release()
    {
        Debug.Assert(_refCount > 0, "Release called more times than AddRef");
        if (--_refCount == 0)
        {
            this.Image = null!;
            _next = null;
        }
    }

    internal bool IsUnreferenced => _refCount == 0;

    // ── Safe chain iteration ──────────────────────────────────────────────

    /// <summary>
    /// Returns a struct-based iterable over the chain from <paramref name="start"/>
    /// through <paramref name="end"/> (inclusive).  If <paramref name="start"/> is
    /// null, the chain contains only <paramref name="end"/>.
    ///
    /// The returned <see cref="SnapshotChain"/> supports <c>foreach</c> with zero
    /// allocation.  It walks the private forward pointers internally, stopping at
    /// <paramref name="end"/> — the caller never touches the forward pointer and
    /// cannot accidentally read past the published frontier.
    /// </summary>
    internal static SnapshotChain Chain(WorldSnapshot? start, WorldSnapshot end) =>
        new(start ?? end, end);

    /// <summary>
    /// A struct-based iterable over a bounded segment of the snapshot chain.
    /// Supports <c>foreach</c> via <see cref="GetEnumerator"/> with zero allocation.
    /// </summary>
    internal readonly struct SnapshotChain
    {
        private readonly WorldSnapshot _start;
        private readonly WorldSnapshot _end;

        internal SnapshotChain(WorldSnapshot start, WorldSnapshot end)
        {
            _start = start;
            _end = end;
        }

        public Enumerator GetEnumerator() => new(_start, _end);

        /// <summary>
        /// Struct enumerator that walks the snapshot chain from start to end
        /// (inclusive).  Accesses the private <c>_next</c> field of
        /// <see cref="WorldSnapshot"/> — safe because this is a nested type.
        /// </summary>
        public struct Enumerator
        {
            private readonly WorldSnapshot _end;
            private WorldSnapshot? _current;
            private bool _started;

            internal Enumerator(WorldSnapshot start, WorldSnapshot end)
            {
                _current = start;
                _end = end;
                _started = false;
            }

            public readonly WorldSnapshot Current => _current!;

            public bool MoveNext()
            {
                if (!_started)
                {
                    _started = true;
                    return _current is not null;
                }

                if (_current is null || ReferenceEquals(_current, _end))
                {
                    _current = null;
                    return false;
                }

                _current = _current._next;
                return _current is not null;
            }
        }
    }
}
