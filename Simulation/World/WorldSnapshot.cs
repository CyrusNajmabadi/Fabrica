using System.Diagnostics;

namespace Simulation.World;

/// <summary>
/// Thin shell wrapping a WorldImage with a forward pointer to the next snapshot.
/// Ref-counted so that future tree-node structural sharing can cascade cleanly.
/// All AddRef/Release calls happen exclusively on the simulation thread — no atomics needed.
/// </summary>
internal sealed class WorldSnapshot
{
    public WorldImage Image = null!;

    // Forward pointer: old → new.  Set once by simulation thread, never changed.
    public WorldSnapshot? Next { get; private set; }

    private int _refCount;

    /// <summary>Called by MemorySystem when renting from pool.</summary>
    internal void Initialize(WorldImage image)
    {
        Debug.Assert(_refCount == 0, "Initialize called on a snapshot still in use");
        Image  = image;
        Next   = null;
        _refCount = 1;
    }

    /// <summary>Links the next snapshot in the chain. Called once per snapshot by simulation thread.</summary>
    internal void SetNext(WorldSnapshot next)
    {
        Debug.Assert(Next is null, "SetNext called more than once");
        Next = next;
    }

    /// <summary>Increments the ref count. Caller guarantees the snapshot is currently live.</summary>
    internal void AddRef()
    {
        Debug.Assert(_refCount > 0, "AddRef on a zero-refcount (freed) snapshot");
        _refCount++;
    }

    /// <summary>
    /// Decrements the ref count.  When it reaches zero the snapshot is ready to be
    /// returned to the pool; caller is responsible for that step.
    /// </summary>
    internal void Release()
    {
        Debug.Assert(_refCount > 0, "Release called more times than AddRef");
        if (--_refCount == 0)
        {
            // Null out refs so pooled instances don't keep objects alive.
            Image = null!;
            Next  = null;
        }
    }

    internal bool IsUnreferenced => _refCount == 0;
}
