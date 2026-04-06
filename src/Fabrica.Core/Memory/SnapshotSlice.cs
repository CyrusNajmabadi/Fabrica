using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Fabrica.Core.Memory;

/// <summary>
/// A typed root set within a snapshot, associated with a single <see cref="GlobalNodeStore{TNode, TNodeOps}"/>.
/// Holds the <see cref="Handle{T}"/> values that are roots for this node type in this snapshot, and provides
/// self-contained lifecycle operations for the build and publish phases.
///
/// LIFECYCLE
///   1. The coordinator rents an <see cref="UnsafeList{T}"/> from its pool and constructs the slice.
///   2. Build phase: call <see cref="AddRoot"/> for each root handle.
///   3. Publish: call <see cref="IncrementRootRefCounts"/> — bumps refcounts so the snapshot "holds" its roots.
///   4. Release (coordinator-driven): the coordinator decrements root refcounts via the store, then takes
///      <see cref="RootHandles"/>, resets it, and returns it to the pool.
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
///   The coordinator owns a pool of <see cref="UnsafeList{T}"/> instances per type. After a snapshot is
///   released, the list is reset and returned to the pool. Steady-state usage incurs zero allocation.
///
/// THREAD MODEL
///   Single-threaded. All operations must come from the coordinator thread. Debug builds assert via
///   the underlying <see cref="GlobalNodeStore{TNode, TNodeOps}"/>'s <see cref="SingleThreadedOwner"/>.
///
/// PORTABILITY
///   In Rust: a struct holding a reference to the <c>GlobalNodeStore</c> and a <c>Vec&lt;Handle&gt;</c> for root handles.
///   In C++: same, with <c>std::vector&lt;Handle&gt;</c>.
/// </summary>
public readonly struct SnapshotSlice<TNode, TNodeOps>(GlobalNodeStore<TNode, TNodeOps> store, UnsafeList<Handle<TNode>> rootHandles)
    where TNode : struct
    where TNodeOps : struct, INodeOps<TNode>
{
    private readonly GlobalNodeStore<TNode, TNodeOps> _store = store;
    private readonly UnsafeList<Handle<TNode>> _rootHandles = rootHandles;

    /// <summary>The underlying root handle list. The coordinator uses this to reset and return the list to
    /// its pool after the snapshot is released.</summary>
    public UnsafeList<Handle<TNode>> RootHandles
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _rootHandles;
    }

    /// <summary>Number of roots in this slice.</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _rootHandles.Count;
    }

    /// <summary>Returns a read-only span over the root handles. Valid until the next <see cref="AddRoot"/>.</summary>
    public ReadOnlySpan<Handle<TNode>> Roots
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _rootHandles.WrittenSpan;
    }

    /// <summary>Adds a handle as a root in this snapshot slice.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void AddRoot(Handle<TNode> handle)
    {
        this.AssertOwnerThread();
        _rootHandles.Add(handle);
    }

    /// <summary>Debug-only assertion that the caller is on the store's owner thread. Delegates to the
    /// underlying <see cref="GlobalNodeStore{TNode, TNodeOps}"/> which maintains the thread-ownership tracking.</summary>
    [Conditional("DEBUG")]
    private readonly void AssertOwnerThread()
        => _store.AssertOwnerThread();

    /// <summary>
    /// Increments the refcount of every root in this slice. Called when the snapshot is published (e.g.,
    /// enqueued into the PCQ). After this call, each root's refcount reflects that this snapshot holds it.
    /// </summary>
    public void IncrementRootRefCounts()
        => _store.IncrementRoots(this.Roots);
}
