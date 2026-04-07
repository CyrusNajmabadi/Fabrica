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
///   <typeparamref name="TNodeOps"/> implements <see cref="INodeOps{TNode}"/> which unifies
///   structural knowledge of which fields are children (<see cref="INodeOps{TNode}.EnumerateChildren{TVisitor}"/>)
///   and decrement dispatch to the correct store per child type (<see cref="INodeVisitor.Visit{T}"/>).
///   This eliminates the need for a separate cascade-free handler interface — the visitor pattern
///   handles everything.
///
/// CASCADE-FREE
///   Owned entirely by this class. When a refcount reaches zero, the cascade loop reads the freed
///   node from the arena, enumerates its children through <typeparamref name="TNodeOps"/> (as both
///   enumerator and visitor), and frees the arena slot. Re-entrant decrements (same-type children)
///   push onto the pending stack and the outer loop processes them — no nested loops, bounded stack
///   depth. Cross-type cascades trigger the other store's cascade via
///   <see cref="INodeVisitor.Visit{T}"/> dispatch.
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
///   For nodes that reference children stored in a different arena (a different <c>GlobalNodeStore</c>),
///   <typeparamref name="TNodeOps"/>'s <see cref="INodeVisitor.Visit{T}"/> dispatches
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
public sealed class GlobalNodeStore<TNode, TNodeOps>
    where TNode : struct
    where TNodeOps : struct, INodeOps<TNode>
{
    private TNodeOps _nodeOps;

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

    /// <summary>
    /// Creates a store with its own arena and refcount table. Call <see cref="SetNodeOps"/> to
    /// complete two-phase initialization before using the store.
    /// </summary>
    public GlobalNodeStore() : this(new UnsafeSlabArena<TNode>(), new RefCountTable<TNode>(), default) { }

    private GlobalNodeStore(UnsafeSlabArena<TNode> arena, RefCountTable<TNode> refCounts, TNodeOps nodeOps)
    {
        this.Arena = arena;
        this.RefCounts = refCounts;
        _nodeOps = nodeOps;
    }

    /// <summary>The slab-backed arena storing nodes of type <typeparamref name="TNode"/>.</summary>
    internal UnsafeSlabArena<TNode> Arena { get; }

    /// <summary>Parallel refcount array — index <c>i</c> holds the refcount for the node at arena index <c>i</c>.</summary>
    internal RefCountTable<TNode> RefCounts { get; }

    /// <summary>Debug-only assertion that the caller is on the owner thread. Delegates to the
    /// underlying <see cref="RefCountTable{T}"/> which holds the sole <see cref="Threading.SingleThreadedOwner"/>.</summary>
    [Conditional("DEBUG")]
    internal void AssertOwnerThread()
        => this.RefCounts.AssertOwnerThread();

    /// <summary>
    /// Sets the node operations struct. Used for two-phase initialization when the ops struct
    /// needs a reference back to this store (for same-type child decrement during cascade).
    /// </summary>
    public void SetNodeOps(TNodeOps ops) => _nodeOps = ops;

    // ── Single-handle operations (used by visitor actions) ─────────────

    /// <summary>Increments the refcount for a single handle. Used by <see cref="INodeVisitor"/> implementations
    /// during child enumeration.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementRefCount(Handle<TNode> handle)
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
    public void DecrementRefCount(Handle<TNode> handle)
    {
        if (!this.RefCounts.Decrement(handle))
            return;

        _cascadePending.Push(handle);

        if (!_cascadeActive)
            this.RunCascade();
    }

    // ── Merge pipeline ─────────────────────────────────────────────────

    /// <summary>
    /// Drains all <see cref="ThreadLocalBuffer{TNode}"/> instances into this store's arena.
    /// For each non-empty TLB: batch-allocates a contiguous range, copies the node data, and
    /// records the local-to-global mapping in <paramref name="remap"/>. Returns the
    /// <c>(startIndex, count)</c> range of newly merged nodes.
    /// </summary>
    public (int StartIndex, int Count) DrainBuffers(ThreadLocalBuffer<TNode>[] tlbs, RemapTable remap)
    {
        // Snapshot where the arena was before we started so we can report the range of newly merged
        // nodes to the caller (they need this to know which nodes to fixup and refcount).
        var startIndex = this.Arena.HighWater;

        // Each worker thread wrote nodes into its own TLB during the parallel work phase. Those nodes
        // still carry local handles (tagged with threadId + localIndex) that are only meaningful within
        // the originating TLB. We need to move them into the single shared arena so that every handle
        // in the system can be resolved to a global arena slot.
        //
        // For each thread's TLB:
        //   1. Reserve a contiguous block of global slots in the arena sized to fit this TLB's output.
        //   2. Copy the raw node data from the TLB into those slots.
        //   3. Record the local→global mapping (thread t, local index i → global batchStart+i) in the
        //      remap table so that RewriteAndIncrementRefCounts can later translate tagged local handles
        //      into their final global indices.
        for (var t = 0; t < tlbs.Length; t++)
        {
            var tlb = tlbs[t];
            if (tlb.Count == 0)
                continue;

            // Reserve tlb.Count contiguous slots starting at batchStart. This is O(1) — just a
            // high-water bump — and guarantees the slots are contiguous for cache-friendly iteration
            // in later phases.
            var batchStart = this.Arena.AllocateBatch(tlb.Count);
            var span = tlb.WrittenSpan;
            for (var i = 0; i < span.Length; i++)
            {
                // Copy node data into its global slot and record where it landed so the remap table
                // can translate any local handle pointing to (thread=t, index=i) into batchStart+i.
                this.Arena[new Handle<TNode>(batchStart + i)] = span[i];
                remap.SetMapping(t, i, batchStart + i);
            }
        }

        // The refcount table must be large enough to cover all the new global indices we just created,
        // otherwise RewriteAndIncrementRefCounts would write out of bounds.
        var count = this.Arena.HighWater - startIndex;
        this.RefCounts.EnsureCapacity(this.Arena.HighWater);

        return (startIndex, count);
    }

    /// <summary>
    /// Rewrites local (tagged) handles to global indices and then increments child refcounts for all
    /// nodes within the range <c>[startIndex, startIndex + count)</c>.
    ///
    /// Phase 1 (rewrite): uses <see cref="INodeOps{TNode}.EnumerateRefChildren{TVisitor}"/> with
    /// <paramref name="remapVisitor"/> to resolve local handles through the remap tables in place.
    ///
    /// Phase 2 (refcount): uses <see cref="INodeOps{TNode}.EnumerateChildren{TVisitor}"/> with
    /// <paramref name="refcountVisitor"/> to increment each child's refcount.
    ///
    /// BARRIER: all types must finish <see cref="DrainBuffers"/> before any type calls this method,
    /// because cross-type handle rewriting needs the target type's remap table.
    /// </summary>
    public void RewriteAndIncrementRefCounts<TRemapVisitor, TRefcountVisitor>(
        int startIndex, int count,
        ref TRemapVisitor remapVisitor,
        ref TRefcountVisitor refcountVisitor)
        where TRemapVisitor : struct, INodeVisitor
        where TRefcountVisitor : struct, INodeVisitor
    {
        // Walk every newly merged node in the arena. At this point the nodes have been copied verbatim
        // from TLBs, so any Handle<T> fields still contain tagged local values (threadId + localIndex).
        // EnumerateRefChildren hands each handle field *by reference* to the visitor, which resolves
        // the local handle through the appropriate remap table and overwrites it in place with the
        // global arena index. After this loop every handle field in the merged range points to a valid
        // global slot.
        for (var i = 0; i < count; i++)
        {
            ref var node = ref this.Arena[new Handle<TNode>(startIndex + i)];
            _nodeOps.EnumerateRefChildren(ref node, ref remapVisitor);
        }

        // Walk every newly merged node again (handles are now global after the rewrite loop above).
        // For each node, EnumerateChildren yields each child handle *by value* to the visitor, which
        // increments that child's refcount in the appropriate RefCountTable. This establishes the
        // structural refcount invariant: every node's RC equals the number of parent fields pointing
        // to it. Root nodes are not yet counted — they get their +1 from IncrementRoots after
        // CollectAndRemapRoots.
        for (var i = 0; i < count; i++)
        {
            ref readonly var node = ref this.Arena[new Handle<TNode>(startIndex + i)];
            _nodeOps.EnumerateChildren(in node, ref refcountVisitor);
        }
    }

    /// <summary>
    /// Collects root handles from all TLBs into the caller-provided <paramref name="destination"/>
    /// list, remapping local handles to global indices via <paramref name="remap"/>. Handles that
    /// are already global (e.g., references to pre-existing nodes from a prior snapshot) pass through
    /// unchanged.
    ///
    /// The caller owns the <see cref="UnsafeList{T}"/> and is responsible for resetting it between
    /// ticks. In steady state this is zero-allocation: the list's backing array grows to the
    /// high-water root count and is reused across ticks.
    /// </summary>
    public void CollectAndRemapRoots(
        ThreadLocalBuffer<TNode>[] tlbs,
        RemapTable remap,
        UnsafeList<Handle<TNode>> destination)
    {
        // Workers marked certain nodes as roots during the parallel phase (via Allocate(isRoot: true)
        // or MarkRoot). Those root handles may be local (pointing into the originating TLB) or already
        // global (referencing a pre-existing node from a prior snapshot that the job decided to re-root).
        // We remap local handles through the same remap table that DrainBuffers built, translating
        // (threadId, localIndex) → globalIndex. Global handles pass through unchanged.
        foreach (var tlb in tlbs)
        {
            foreach (var handle in tlb.RootHandles)
            {
                var index = handle.Index;
                if (TaggedHandle.IsLocal(index))
                {
                    var threadId = TaggedHandle.DecodeThreadId(index);
                    var localIndex = TaggedHandle.DecodeLocalIndex(index);
                    destination.Add(new Handle<TNode>(remap.Resolve(threadId, localIndex)));
                }
                else
                {
                    destination.Add(handle);
                }
            }
        }
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

    internal readonly struct TestAccessor(GlobalNodeStore<TNode, TNodeOps> store)
    {
        /// <summary>
        /// Creates a store with caller-provided arena and refcount table so tests can inspect
        /// internal state. Call <see cref="GlobalNodeStore{TNode,TNodeOps}.SetNodeOps"/> on the
        /// returned store to complete two-phase initialization.
        /// </summary>
        public static GlobalNodeStore<TNode, TNodeOps> Create(
            UnsafeSlabArena<TNode> arena, RefCountTable<TNode> refCounts)
            => new(arena, refCounts, default);

        public UnsafeSlabArena<TNode> Arena => store.Arena;
        public RefCountTable<TNode> RefCounts => store.RefCounts;
        public TNodeOps NodeOps => store._nodeOps;
        public bool CascadeActive => store._cascadeActive;
    }
}
