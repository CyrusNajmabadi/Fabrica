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
///   Only the simulation thread reads or writes the chain.  The Next pointer and
///   ref-count are therefore not synchronised — no atomics, no locks needed.
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
    public WorldImage Image = null!;

    // Forward pointer: old → new.
    // Set once via SetNext; may be cleared via ClearNext when the snapshot is extracted
    // from the live chain into the pinned queue.
    public WorldSnapshot? Next { get; private set; }

    private int _refCount;

    /// <summary>Called by MemorySystem when renting from pool.</summary>
    internal void Initialize(WorldImage image)
    {
        Debug.Assert(_refCount == 0, "Initialize called on a snapshot still in use");
        Image     = image;
        Next      = null;
        _refCount = 1;
    }

    /// <summary>Links the next snapshot in the chain. Called once per snapshot by the simulation thread.</summary>
    internal void SetNext(WorldSnapshot next)
    {
        Debug.Assert(Next is null, "SetNext called more than once on the same snapshot");
        Next = next;
    }

    /// <summary>
    /// Severs the forward pointer when this snapshot is extracted from the live chain
    /// into the pinned queue, so that the snapshots that followed it can be freed normally.
    /// </summary>
    internal void ClearNext()
    {
        Next = null;
    }

    internal void AddRef()
    {
        Debug.Assert(_refCount > 0, "AddRef on a zero-refcount (freed) snapshot");
        _refCount++;
    }

    /// <summary>
    /// Decrements the ref count.  When it reaches zero the caller must return this
    /// snapshot to the pool.  Nulls out Image and Next so pooled instances do not
    /// keep objects alive unnecessarily.
    /// </summary>
    internal void Release()
    {
        Debug.Assert(_refCount > 0, "Release called more times than AddRef");
        if (--_refCount == 0)
        {
            Image = null!;
            Next  = null;
        }
    }

    internal bool IsUnreferenced => _refCount == 0;
}
