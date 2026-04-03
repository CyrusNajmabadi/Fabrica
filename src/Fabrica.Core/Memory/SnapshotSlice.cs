using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Fabrica.Core.Memory;

/// <summary>
/// A typed root set within a snapshot, associated with a single <see cref="NodeStore{TNode, THandler}"/>.
/// Holds the <see cref="Handle{T}"/> values that are roots for this node type in this snapshot, and provides
/// self-contained lifecycle operations.
///
/// LIFECYCLE
///   1. Build phase: call <see cref="AddRoot"/> for each root handle.
///   2. Publish: call <see cref="IncrementRootRefCounts"/> — bumps refcounts so the snapshot "holds" its roots.
///   3. Release: call <see cref="DecrementRootRefCounts"/> — decrements refcounts, triggering cascade-free
///      for any that hit zero.
///   4. Reuse: call <see cref="Clear"/> to reset for the next snapshot without releasing backing storage.
///
/// REUSE
///   The internal <see cref="List{T}"/> grows to steady state and is reused via <see cref="Clear"/>.
///   Once a snapshot is fully released, its slices can be reused by future snapshots with zero allocation.
///
/// PORTABILITY
///   In Rust: a struct holding a reference to the <c>NodeStore</c> and a <c>Vec&lt;Handle&gt;</c> for root handles.
///   In C++: same, with <c>std::vector&lt;Handle&gt;</c>.
/// </summary>
internal readonly struct SnapshotSlice<TNode, THandler>(NodeStore<TNode, THandler> store, List<Handle<TNode>> rootHandles)
    where TNode : struct
    where THandler : struct, RefCountTable<TNode>.IRefCountHandler
{
    private readonly NodeStore<TNode, THandler> _store = store;
    private readonly List<Handle<TNode>> _rootHandles = rootHandles;

    public SnapshotSlice(NodeStore<TNode, THandler> store)
        : this(store, [])
    {
    }

    /// <summary>Number of roots in this slice.</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _rootHandles.Count;
    }

    /// <summary>Returns a read-only span over the root handles. Valid until the next <see cref="AddRoot"/> or
    /// <see cref="Clear"/>.</summary>
    public ReadOnlySpan<Handle<TNode>> Roots
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => CollectionsMarshal.AsSpan(_rootHandles);
    }

    /// <summary>Adds a handle as a root in this snapshot slice.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void AddRoot(Handle<TNode> handle)
        => _rootHandles.Add(handle);

    /// <summary>Resets the root list for reuse in the next snapshot. The backing array is retained so
    /// future snapshots allocate nothing in steady state.</summary>
    public readonly void Clear()
        => _rootHandles.Clear();

    /// <summary>
    /// Increments the refcount of every root in this slice. Called when the snapshot is published (e.g.,
    /// enqueued into the PCQ). After this call, each root's refcount reflects that this snapshot holds it.
    /// </summary>
    public void IncrementRootRefCounts()
        => _store.IncrementRoots(this.Roots);

    /// <summary>
    /// Decrements the refcount of every root in this slice and triggers cascade-free for any that hit zero.
    /// Called when the snapshot is released (e.g., retired by the consumer). Uses the handler stored in the
    /// <see cref="NodeStore{TNode, THandler}"/> — no external handler needed.
    /// </summary>
    public void DecrementRootRefCounts()
        => _store.DecrementRoots(this.Roots);
}
