using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Fabrica.Core.Memory;

/// <summary>
/// Bundles the three components needed to fully manage a single node type's lifecycle: storage
/// (<see cref="UnsafeSlabArena{T}"/>), reference counts (<see cref="RefCountTable{T}"/>), and
/// node operations (<typeparamref name="TNodeOps"/>). One instance per node type, shared
/// across all snapshots.
///
/// NODE OPERATIONS
///   <typeparamref name="TNodeOps"/> implements both <see cref="INodeChildEnumerator{TNode}"/>
///   (structural knowledge of which fields are children) and <see cref="INodeVisitor"/>
///   (decrement dispatch to the correct store per child type). This eliminates the need for a
///   separate cascade-free handler interface — the visitor pattern handles everything.
///
/// CASCADE-FREE
///   Owned entirely by this class. When a refcount reaches zero, the cascade loop reads the freed
///   node from the arena, enumerates its children through <typeparamref name="TNodeOps"/> (as both
///   enumerator and visitor), and frees the arena slot. Re-entrant decrements (same-type children)
///   push onto the pending stack and the outer loop processes them — no nested loops, bounded stack
///   depth. Cross-type cascades trigger the other store's cascade via
///   <see cref="INodeVisitor.Visit{TChild}"/> dispatch.
///
/// ROOT OPERATIONS
///   <see cref="IncrementRoots"/> and <see cref="DecrementRoots"/> provide self-contained batch
///   refcount updates for snapshot root sets.
///
/// VALIDATION (DEBUG ONLY)
///   Call <see cref="EnableValidation"/> in tests to activate automatic DAG invariant checking
///   after every <see cref="IncrementRoots"/> and <see cref="DecrementRoots"/> call. The store
///   uses its own <typeparamref name="TNodeOps"/> as the enumerator — no separate enumerator needed.
///
/// CROSS-TYPE DAGS
///   For nodes that reference children stored in a different arena (a different <c>NodeStore</c>),
///   <typeparamref name="TNodeOps"/>'s <see cref="INodeVisitor.Visit{TChild}"/> dispatches
///   to the correct store using <c>typeof</c> checks (JIT-eliminated dead branches).
///
/// TWO-PHASE INITIALIZATION
///   When <typeparamref name="TNodeOps"/> needs a reference back to this store (for same-type
///   cascade), construct the store with <c>default</c> ops, then call <see cref="SetNodeOps"/>
///   with an ops instance that captures the store reference.
///
/// THREAD MODEL
///   Single-threaded. All operations must come from the coordinator thread. Debug builds assert via
///   the underlying <see cref="RefCountTable{T}"/>'s <see cref="Threading.SingleThreadedOwner"/>.
///
/// PORTABILITY
///   No GC reliance. In Rust: a struct holding the arena, refcount table, and a handler function
///   pointer or trait object. In C++: a class owning the arena + refcount table with a templated
///   handler.
/// </summary>
internal sealed class NodeStore<TNode, TNodeOps>(UnsafeSlabArena<TNode> arena, RefCountTable<TNode> refCounts, TNodeOps nodeOps)
    where TNode : struct
    where TNodeOps : struct, INodeChildEnumerator<TNode>, INodeVisitor
{
    private TNodeOps _nodeOps = nodeOps;

    /// <summary>
    /// Handles whose refcount reached zero during a decrement cascade, pending processing.
    /// Reused across cascade operations to avoid allocation.
    /// </summary>
    private readonly UnsafeStack<Handle<TNode>> _cascadePending = new();

    /// <summary>
    /// True while a decrement cascade is being processed. When a <see cref="DecrementRefCount"/>
    /// call hits zero during an active cascade (e.g., a same-type child decrement, or a cross-store
    /// A→B→A bounce), the handle is pushed onto <see cref="_cascadePending"/> and the caller returns
    /// immediately — the outer cascade loop will process it.
    /// </summary>
    private bool _cascadeActive;

#if DEBUG
    private Dictionary<Handle<TNode>, int>? _trackedRootCounts;
    private Action? _runValidation;
#endif

    /// <summary>The slab-backed arena storing nodes of type <typeparamref name="TNode"/>.</summary>
    public UnsafeSlabArena<TNode> Arena { get; } = arena;

    /// <summary>Parallel refcount array — index <c>i</c> holds the refcount for the node at arena index <c>i</c>.</summary>
    public RefCountTable<TNode> RefCounts { get; } = refCounts;

    /// <summary>Debug-only assertion that the caller is on the owner thread. Delegates to the
    /// underlying <see cref="RefCountTable{T}"/> which holds the sole <see cref="Threading.SingleThreadedOwner"/>.</summary>
    [Conditional("DEBUG")]
    internal void AssertOwnerThread()
        => this.RefCounts.AssertOwnerThread();

    /// <summary>
    /// Sets the node operations struct. Used for two-phase initialization when the ops struct
    /// needs a reference back to this store (for same-type child decrement during cascade).
    /// </summary>
    internal void SetNodeOps(TNodeOps ops) => _nodeOps = ops;

    // ── Single-handle operations (used by visitor actions) ─────────────

    /// <summary>Increments the refcount for a single handle. Used by <see cref="INodeVisitor"/> implementations
    /// during child enumeration.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void IncrementRefCount(Handle<TNode> handle)
        => this.RefCounts.Increment(handle);

    /// <summary>
    /// Decrements the refcount for a single handle. If it reaches zero, the handle is pushed onto
    /// the cascade pending stack. If no cascade is already active, starts the cascade loop which
    /// reads each freed node, enumerates its children via <typeparamref name="TNodeOps"/>, and frees
    /// the arena slot.
    ///
    /// Re-entrant calls (from within the cascade loop for same-type children, or from cross-store
    /// A→B→A bounces) push onto the existing pending stack instead of starting a nested loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void DecrementRefCount(Handle<TNode> handle)
    {
        if (!this.RefCounts.Decrement(handle))
            return;

        _cascadePending.Push(handle);

        if (!_cascadeActive)
            this.RunCascade();
    }

    // ── Root operations ──────────────────────────────────────────────────

    /// <summary>
    /// Increments the refcount for each root handle. Called when a snapshot holding these roots is published
    /// (e.g., enqueued into the PCQ). The caller must have called <see cref="RefCountTable{T}.EnsureCapacity"/>
    /// for the index range beforehand.
    /// </summary>
    public void IncrementRoots(ReadOnlySpan<Handle<TNode>> roots)
    {
        this.RefCounts.IncrementBatch(roots);
        this.TrackAndValidateAfterIncrement(roots);
    }

    /// <summary>
    /// Decrements the refcount for each root handle and cascades any that hit zero. Called when a
    /// snapshot holding these roots is released (e.g., retired by the consumer after processing).
    /// </summary>
    public void DecrementRoots(ReadOnlySpan<Handle<TNode>> roots)
    {
        this.TrackBeforeDecrement(roots);
        Debug.Assert(!_cascadeActive, "DecrementRoots must not be called during an active cascade.");
        this.RefCounts.DecrementBatch(roots, _cascadePending);
        this.RunCascade();
        this.RunValidation();
    }

    // ── Cascade loop ─────────────────────────────────────────────────────

    /// <summary>
    /// Iteratively processes all handles whose refcount reached zero. For each freed handle: reads
    /// the node from the arena, enumerates children using <typeparamref name="TNodeOps"/> as both
    /// enumerator and decrement visitor, then frees the arena slot.
    /// </summary>
    private void RunCascade()
    {
        if (_cascadePending.Count == 0)
            return;

        _cascadeActive = true;

        while (_cascadePending.TryPop(out var current))
        {
            ref readonly var node = ref this.Arena[current];
            var ops = _nodeOps;
            ops.EnumerateChildren(in node, ref ops);
            this.Arena.Free(current);
        }

        _cascadeActive = false;
    }

    // ── Validation (debug only) ──────────────────────────────────────────

    /// <summary>
    /// Enables automatic DAG invariant checking (debug builds only). After every <see cref="IncrementRoots"/>
    /// and <see cref="DecrementRoots"/>, the store runs <see cref="DagValidator.AssertValid"/> in relaxed
    /// mode with the current set of tracked roots. Uses the stored <typeparamref name="TNodeOps"/> as
    /// the enumerator. Compiled out entirely in release builds.
    /// </summary>
    [Conditional("DEBUG")]
    internal void EnableValidation()
    {
#if DEBUG
        _trackedRootCounts = [];
        _runValidation = () =>
        {
            var roots = this.ExpandTrackedRoots();
            DagValidator.AssertValid(this, roots, _nodeOps, strict: false);
        };
#endif
    }

#if DEBUG
    private Handle<TNode>[] ExpandTrackedRoots()
    {
        var counts = _trackedRootCounts!;
        var total = 0;
        foreach (var count in counts.Values)
            total += count;

        var result = new Handle<TNode>[total];
        var pos = 0;
        foreach (var (handle, count) in counts)
        {
            for (var i = 0; i < count; i++)
                result[pos++] = handle;
        }

        return result;
    }
#endif

    [Conditional("DEBUG")]
    private void TrackAndValidateAfterIncrement(ReadOnlySpan<Handle<TNode>> roots)
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
    private void TrackBeforeDecrement(ReadOnlySpan<Handle<TNode>> roots)
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

    internal readonly struct TestAccessor(NodeStore<TNode, TNodeOps> store)
    {
        public TNodeOps NodeOps => store._nodeOps;
        public bool CascadeActive => store._cascadeActive;
    }
}
