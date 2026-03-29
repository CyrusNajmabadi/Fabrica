using System.Diagnostics;

namespace Simulation.World;

/// <summary>
/// Thin shell wrapping a WorldImage with a forward pointer to the next snapshot.
/// Ref-counted to support future tree-node structural sharing cleanup cascades.
/// All AddRef/Release calls happen exclusively on the simulation thread — no atomics needed.
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
