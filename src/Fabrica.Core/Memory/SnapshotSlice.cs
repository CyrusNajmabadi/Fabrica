using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Fabrica.Core.Memory;

/// <summary>
/// A typed root set within a snapshot, associated with a single <see cref="NodeStore{TNode, TNodeOps}"/>.
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
/// ROOT SEMANTICS
///   Roots are per-snapshot. Each snapshot must explicitly declare its own root set — roots from prior
///   snapshots are NOT inherited. If a node should remain a root across ticks, jobs must re-mark it every
///   tick. This ensures that when a snapshot is released, only its declared roots are decremented, and nodes
///   that are no longer roots in newer snapshots become eligible for cascade-free once all pinning snapshots
///   are released. A root handle may refer to any existing node, including one that was an internal (non-root)
///   node in a prior snapshot — the refcount system composes overlapping pins correctly.
///
/// REUSE
///   The internal <see cref="List{T}"/> grows to steady state and is reused via <see cref="Clear"/>.
///   Once a snapshot is fully released, its slices can be reused by future snapshots with zero allocation.
///
/// THREAD MODEL
///   Single-threaded. All operations must come from the coordinator thread. Debug builds assert via
///   the underlying <see cref="NodeStore{TNode, TNodeOps}"/>'s <see cref="SingleThreadedOwner"/>.
///
/// PORTABILITY
///   In Rust: a struct holding a reference to the <c>NodeStore</c> and a <c>Vec&lt;Handle&gt;</c> for root handles.
///   In C++: same, with <c>std::vector&lt;Handle&gt;</c>.
/// </summary>
internal readonly struct SnapshotSlice<TNode, TNodeOps>(NodeStore<TNode, TNodeOps> store, List<Handle<TNode>> rootHandles)
    where TNode : struct
    where TNodeOps : struct, INodeOps<TNode>
{
    private readonly NodeStore<TNode, TNodeOps> _store = store;
    private readonly List<Handle<TNode>> _rootHandles = rootHandles;

    public SnapshotSlice(NodeStore<TNode, TNodeOps> store)
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
    {
        this.AssertOwnerThread();
        _rootHandles.Add(handle);
    }

    /// <summary>Resets the root list for reuse in the next snapshot. The backing array is retained so
    /// future snapshots allocate nothing in steady state.</summary>
    public readonly void Clear()
    {
        this.AssertOwnerThread();
        _rootHandles.Clear();
    }

    /// <summary>Debug-only assertion that the caller is on the store's owner thread. Delegates to the
    /// underlying <see cref="NodeStore{TNode, TNodeOps}"/> which maintains the thread-ownership tracking.</summary>
    [Conditional("DEBUG")]
    private readonly void AssertOwnerThread()
        => _store.AssertOwnerThread();

    /// <summary>
    /// Increments the refcount of every root in this slice. Called when the snapshot is published (e.g.,
    /// enqueued into the PCQ). After this call, each root's refcount reflects that this snapshot holds it.
    /// </summary>
    public void IncrementRootRefCounts()
        => _store.IncrementRoots(this.Roots);

    /// <summary>
    /// Decrements the refcount of every root in this slice and triggers cascade-free for any that hit zero.
    /// Called when the snapshot is released (e.g., retired by the consumer). Uses the node operations stored
    /// in the <see cref="NodeStore{TNode, TNodeOps}"/> — no external handler needed.
    /// </summary>
    public void DecrementRootRefCounts()
        => _store.DecrementRoots(this.Roots);
}
