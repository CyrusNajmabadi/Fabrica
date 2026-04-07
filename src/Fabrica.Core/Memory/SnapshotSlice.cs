using System.Runtime.CompilerServices;
using Fabrica.Core.Collections.Unsafe;
using Fabrica.Core.Memory.Nodes;

namespace Fabrica.Core.Memory;

/// <summary>
/// A typed root set within a snapshot, associated with a single <see cref="GlobalNodeStore{TNode, TNodeOps}"/>.
/// Holds the <see cref="Handle{T}"/> values that are roots for this node type in this snapshot.
///
/// LIFECYCLE
///   Created by <see cref="GlobalNodeStore{TNode,TNodeOps}.BuildSnapshotSlice"/>, which increments
///   root refcounts before handing off the slice. The slice is an ownership token: its roots are
///   already pinned at construction time.
///   Release: call <see cref="GlobalNodeStore{TNode,TNodeOps}.ReleaseSnapshotSlice"/> — decrements
///   root refcounts and cascades freed nodes.
///
/// ROOT SEMANTICS
///   Roots are per-snapshot. Each snapshot must explicitly declare its own root set — roots from prior
///   snapshots are NOT inherited. If a node should remain a root across ticks, jobs must re-mark it every
///   tick. This ensures that when a snapshot is released, only its declared roots are decremented, and nodes
///   that are no longer roots in newer snapshots become eligible for cascade-free once all pinning snapshots
///   are released. A root handle may refer to any existing node, including one that was an internal (non-root)
///   node in a prior snapshot — the refcount system composes overlapping pins correctly.
///
/// THREAD MODEL
///   Single-threaded. All operations must come from the coordinator thread. Debug builds assert via
///   the underlying <see cref="GlobalNodeStore{TNode, TNodeOps}"/>'s <see cref="SingleThreadedOwner"/>.
///
/// PORTABILITY
///   In Rust: a struct holding a reference to the <c>GlobalNodeStore</c> and a <c>Vec&lt;Handle&gt;</c> for root handles.
///   In C++: same, with <c>std::vector&lt;Handle&gt;</c>.
/// </summary>
public readonly struct SnapshotSlice<TNode, TNodeOps>
    where TNode : struct
    where TNodeOps : struct, INodeOps<TNode>
{
    private readonly GlobalNodeStore<TNode, TNodeOps> _store;
    private readonly UnsafeList<Handle<TNode>> _rootHandles;

    internal SnapshotSlice(GlobalNodeStore<TNode, TNodeOps> store, UnsafeList<Handle<TNode>> rootHandles)
    {
        _store = store;
        _rootHandles = rootHandles;
    }

    /// <summary>Number of roots in this slice.</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _rootHandles.Count;
    }

    /// <summary>Returns a read-only span over the root handles.</summary>
    public ReadOnlySpan<Handle<TNode>> Roots
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _rootHandles.WrittenSpan;
    }

    /// <summary>Resets the root list and returns it so the caller can recycle the backing storage.</summary>
    internal UnsafeList<Handle<TNode>> DetachRoots()
    {
        _rootHandles.Reset();
        return _rootHandles;
    }
}
