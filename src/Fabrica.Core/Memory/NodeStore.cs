using System.Diagnostics;
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
/// VALIDATION (DEBUG ONLY)
///   Call <see cref="EnableValidation{TEnumerator}"/> in tests to activate automatic DAG invariant
///   checking after every <see cref="IncrementRoots"/> and <see cref="DecrementRoots"/> call. The
///   store tracks the multi-set of active roots and runs <see cref="DagValidator.AssertValid"/> after
///   each mutation. All validation state and logic is compiled out in release builds.
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

#if DEBUG
    private Dictionary<int, int>? _trackedRootCounts;
    private Action? _runValidation;
#endif

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
        this.RunValidation();
    }

    // ── Validation (debug only) ──────────────────────────────────────────

    /// <summary>
    /// Enables automatic DAG invariant checking (debug builds only). After every <see cref="IncrementRoots"/>
    /// and <see cref="DecrementRoots"/>, the store runs <see cref="DagValidator.AssertValid"/> in relaxed
    /// mode with the current set of tracked roots. Compiled out entirely in release builds.
    /// </summary>
    [Conditional("DEBUG")]
    internal void EnableValidation<TEnumerator>(TEnumerator enumerator)
        where TEnumerator : struct, DagValidator.IChildEnumerator<TNode>
    {
#if DEBUG
        _trackedRootCounts = [];
        _runValidation = () =>
        {
            var roots = this.ExpandTrackedRoots();
            DagValidator.AssertValid(this, roots, enumerator, strict: false);
        };
#endif
    }

#if DEBUG
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
#endif

    [Conditional("DEBUG")]
    private void TrackAndValidateAfterIncrement(ReadOnlySpan<int> roots)
    {
#if DEBUG
        if (_trackedRootCounts == null)
            return;

        for (var i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            CollectionsMarshal.GetValueRefOrAddDefault(_trackedRootCounts, root, out _) += 1;
        }

        _runValidation!();
#endif
    }

    [Conditional("DEBUG")]
    private void TrackBeforeDecrement(ReadOnlySpan<int> roots)
    {
#if DEBUG
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
#endif
    }

    [Conditional("DEBUG")]
    private void RunValidation()
    {
#if DEBUG
        _runValidation?.Invoke();
#endif
    }

    // ── Test accessor ─────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor()
        => new(this);

    internal readonly struct TestAccessor(NodeStore<TNode, THandler> store)
    {
        public THandler Handler => store._handler;
    }
}
