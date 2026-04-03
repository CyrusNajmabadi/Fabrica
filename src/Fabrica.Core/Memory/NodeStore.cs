using System.Runtime.InteropServices;
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
/// VALIDATION
///   Call <see cref="EnableValidation{TEnumerator}"/> in tests to activate automatic DAG invariant
///   checking after every <see cref="IncrementRoots"/> and <see cref="DecrementRoots"/> call. The
///   store tracks the multi-set of active roots and runs <see cref="DagValidator.AssertValid"/> after
///   each mutation. Not for production hot paths.
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

    /// <summary>
    /// Multi-set of active root indices (root → hold count). Null when validation is disabled.
    /// Populated by <see cref="IncrementRoots"/> and depopulated by <see cref="DecrementRoots"/>
    /// so the validator always sees the current set of live roots.
    /// </summary>
    private Dictionary<int, int>? _trackedRootCounts;

    /// <summary>
    /// Validation callback, set by <see cref="EnableValidation{TEnumerator}"/>. Captures the child
    /// enumerator in a closure so validation can run without the caller passing it each time.
    /// </summary>
    private Action? _runValidation;

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
        this.TrackAndValidateAfterIncrement(roots);
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
        this.TrackBeforeDecrement(roots);
        this.RefCounts.DecrementBatch(roots, _handler);
        _runValidation?.Invoke();
    }

    // ── Validation ───────────────────────────────────────────────────────

    /// <summary>
    /// Enables automatic DAG invariant checking. After every <see cref="IncrementRoots"/> and
    /// <see cref="DecrementRoots"/>, the store runs <see cref="DagValidator.AssertValid"/> in relaxed
    /// mode with the current set of tracked roots. Relaxed mode checks <c>actual &gt;= expected</c>
    /// (tolerating cross-store references and untracked roots) rather than exact match. Call once
    /// during test setup.
    /// </summary>
    internal void EnableValidation<TEnumerator>(TEnumerator enumerator)
        where TEnumerator : struct, DagValidator.IChildEnumerator<TNode>
    {
        _trackedRootCounts = [];
        _runValidation = () =>
        {
            var roots = this.ExpandTrackedRoots();
            DagValidator.AssertValid(this, roots, enumerator, strict: false);
        };
    }

    /// <summary>Expands the multi-set of tracked roots into a flat array for the validator.</summary>
    private int[] ExpandTrackedRoots()
    {
        var counts = _trackedRootCounts!;
        var total = 0;
        foreach (var count in counts.Values)
            total += count;

        var result = new int[total];
        var pos = 0;
        foreach (var (index, count) in counts)
        {
            for (var i = 0; i < count; i++)
                result[pos++] = index;
        }

        return result;
    }

    private void TrackAndValidateAfterIncrement(ReadOnlySpan<int> roots)
    {
        if (_trackedRootCounts == null)
            return;

        for (var i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            CollectionsMarshal.GetValueRefOrAddDefault(_trackedRootCounts, root, out _) += 1;
        }

        _runValidation!();
    }

    private void TrackBeforeDecrement(ReadOnlySpan<int> roots)
    {
        if (_trackedRootCounts == null)
            return;

        for (var i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            ref var count = ref CollectionsMarshal.GetValueRefOrNullRef(_trackedRootCounts, root);
            count--;
            if (count <= 0)
                _trackedRootCounts.Remove(root);
        }
    }

    // ── Test accessor ─────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor()
        => new(this);

    internal readonly struct TestAccessor(NodeStore<TNode, THandler> store)
    {
        public THandler Handler => store._handler;
    }
}
