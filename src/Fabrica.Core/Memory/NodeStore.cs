using Fabrica.Core.Threading;

namespace Fabrica.Core.Memory;

/// <summary>
/// Bundles the three components needed to fully manage a single node type's lifecycle: storage
/// (<see cref="UnsafeSlabArena{T}"/>), reference counts (<see cref="RefCountTable"/>), and a
/// cascade-free handler (<typeparamref name="THandler"/>). One instance per node type, shared
/// across all snapshots.
///
/// ROOT OPERATIONS
///   <see cref="IncrementRoots"/> and <see cref="DecrementRoots"/> provide self-contained batch
///   refcount updates for snapshot root sets. The stored handler enables <see cref="DecrementRoots"/>
///   to trigger cascade-free without the caller needing to construct or pass a handler.
///
/// CROSS-TYPE DAGS
///   For nodes that reference children stored in a different arena (a different <c>NodeStore</c>),
///   <typeparamref name="THandler"/> captures references to those other stores. When
///   <see cref="RefCountTable.IRefCountHandler.OnFreed"/> fires, the handler decrements children in
///   whatever stores they belong to.
///
/// THREAD MODEL
///   Single-threaded. All operations must come from the coordinator thread. Debug builds assert via
///   <see cref="SingleThreadedOwner"/>.
///
/// PORTABILITY
///   No GC reliance. In Rust: a struct holding the arena, refcount table, and a handler function
///   pointer or trait object. In C++: a class owning the arena + refcount table with a templated
///   handler.
/// </summary>
internal sealed class NodeStore<TNode, THandler>(UnsafeSlabArena<TNode> arena, RefCountTable refCounts, THandler handler)
    where TNode : struct
    where THandler : struct, RefCountTable.IRefCountHandler
{
    private readonly THandler _handler = handler;
    private SingleThreadedOwner _owner;

    /// <summary>The slab-backed arena storing nodes of type <typeparamref name="TNode"/>.</summary>
    public UnsafeSlabArena<TNode> Arena { get; } = arena;

    /// <summary>Parallel refcount array — index <c>i</c> holds the refcount for the node at arena index <c>i</c>.</summary>
    public RefCountTable RefCounts { get; } = refCounts;

    // ── Root operations ──────────────────────────────────────────────────

    /// <summary>
    /// Increments the refcount for each root index. Called when a snapshot holding these roots is published
    /// (e.g., enqueued into the PCQ). The caller must have called <see cref="RefCountTable.EnsureCapacity"/>
    /// for the index range beforehand.
    /// </summary>
    public void IncrementRoots(ReadOnlySpan<int> roots)
    {
        _owner.AssertOwnerThread();
        this.RefCounts.IncrementBatch(roots);
    }

    /// <summary>
    /// Decrements the refcount for each root index and triggers cascade-free for any that hit zero.
    /// Called when a snapshot holding these roots is released (e.g., retired by the consumer after
    /// processing). Uses the stored <typeparamref name="THandler"/> for cascade — no caller involvement
    /// needed.
    /// </summary>
    public void DecrementRoots(ReadOnlySpan<int> roots)
    {
        _owner.AssertOwnerThread();
        this.RefCounts.DecrementBatch(roots, _handler);
    }

    // ── Test accessor ─────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor()
        => new(this);

    internal readonly struct TestAccessor(NodeStore<TNode, THandler> store)
    {
        public THandler Handler => store._handler;
    }
}
