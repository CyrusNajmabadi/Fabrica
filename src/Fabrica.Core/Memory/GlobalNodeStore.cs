using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fabrica.Core.Collections.Unsafe;
using Fabrica.Core.Memory.Nodes;

namespace Fabrica.Core.Memory;

/// <summary>
/// Non-generic base for <see cref="GlobalNodeStore{TNode,TNodeOps}"/>. Lets
/// <see cref="MergeCoordinator"/> orchestrate drain, rewrite, refcount, and reset across
/// stores without knowing the concrete node types.
/// </summary>
public abstract class GlobalNodeStore
{
    internal abstract void Drain();
    internal abstract void RewriteAndIncrementRefCounts();
    internal abstract void ResetMergeState();

#if DEBUG
    private bool _isMergeActive;
#endif

    [Conditional("DEBUG")]
    internal void SetMergeActive(bool value)
    {
#if DEBUG
        Debug.Assert(_isMergeActive != value, $"Expected IsMergeActive to be {!value} but was {value}.");
        _isMergeActive = value;
#endif
    }

    [Conditional("DEBUG")]
    internal void AssertMergeActive(
        [CallerMemberName] string caller = "")
    {
#if DEBUG
        Debug.Assert(_isMergeActive, $"{caller} must be called within a MergeTransaction scope.");
#endif
    }
}

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
///
/// ROOT OPERATIONS
///   <see cref="BuildSnapshotSlice"/> increments root refcounts when creating a snapshot.
///   <see cref="ReleaseSnapshotSlice"/> decrements root refcounts and cascades freed nodes.
///
/// VALIDATION (DEBUG ONLY)
///   Call <see cref="TestAccessor.EnableValidation"/> in tests to activate automatic DAG invariant checking
///   after every root increment/decrement.
///
/// CROSS-TYPE DAGS
///   For nodes that reference children stored in a different arena (a different <c>GlobalNodeStore</c>),
///   <typeparamref name="TNodeOps"/>'s <see cref="INodeVisitor.Visit{T}"/> dispatches
///   to the correct store.
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
public sealed class GlobalNodeStore<TNode, TNodeOps> : GlobalNodeStore
    where TNode : struct
    where TNodeOps : struct, INodeOps<TNode>
{
    private TNodeOps _nodeOps;

    /// <summary>
    /// Handles whose refcount reached zero during a decrement cascade, pending processing.
    /// Reused across cascade operations to avoid allocation.
    /// </summary>
    private readonly UnsafeStack<Handle<TNode>> _cascadePending = new();

    /// <summary>True while a decrement cascade is being processed.</summary>
    private bool _cascadeActive;

    /// <summary>Pool of <see cref="UnsafeList{T}"/> instances for root handle collection during
    /// <see cref="BuildSnapshotSlice"/>. Instances are returned by <see cref="ReleaseSnapshotSlice"/>,
    /// so backing arrays are retained across ticks for zero steady-state allocation.</summary>
    private readonly UnsafeStack<UnsafeList<Handle<TNode>>> _rootListPool = new();

    /// <summary>Per-worker append buffers for nodes created during the parallel work phase.
    /// Empty array when the store has no merge infrastructure (test-only parameterless constructor).</summary>
    private readonly ThreadLocalBuffer<TNode>[] _threadLocalBuffers;

    /// <summary>Maps <c>(threadId, localIndex)</c> pairs to global arena indices during merge.
    /// Default (no-op) when the store has no merge infrastructure.</summary>
    private readonly RemapTable _remap;

    /// <summary>Starting global index of the most recent <see cref="DrainBuffers"/> batch.
    /// Read by <see cref="RewriteAndIncrementRefCounts"/> to iterate the newly drained range.</summary>
    private int _lastDrainStart;

    /// <summary>Number of nodes in the most recent <see cref="DrainBuffers"/> batch.</summary>
    private int _lastDrainCount;

#if DEBUG
    /// <summary>Running root-handle reference counts for debug validation. Each
    /// <see cref="IncrementRoots"/> increments and each <see cref="DecrementRoots"/> decrements.
    /// Null until <see cref="TestAccessor.EnableValidation"/> is called.</summary>
    private Dictionary<Handle<TNode>, int>? _trackedRootCounts;

    /// <summary>Delegate that runs <see cref="DagValidator.AssertValid"/> against the current
    /// tracked roots. Null until <see cref="TestAccessor.EnableValidation"/> is called.</summary>
    private Action? _runValidation;
#endif

    /// <summary>
    /// Creates a store with per-worker merge infrastructure (thread-local buffers and remap table).
    /// Call <see cref="SetNodeOps"/> to complete two-phase initialization before using the store.
    /// </summary>
    public GlobalNodeStore(int workerCount) : this()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        _threadLocalBuffers = new ThreadLocalBuffer<TNode>[workerCount];
        for (var i = 0; i < workerCount; i++)
            _threadLocalBuffers[i] = new ThreadLocalBuffer<TNode>(i);
        _remap = new RemapTable(workerCount);
    }

    /// <summary>
    /// Creates a store without merge infrastructure. Called by <see cref="GlobalNodeStore(int)"/>
    /// for shared initialization and by <see cref="TestAccessor.Create"/> for test-only stores.
    /// </summary>
    private GlobalNodeStore()
    {
        this.Arena = new UnsafeSlabArena<TNode>();
        this.RefCounts = new RefCountTable<TNode>();
        _nodeOps = default;
        _threadLocalBuffers = [];
        _remap = default;
    }

    /// <summary>Test-only: creates a store with caller-provided arena and refcount table.</summary>
    private GlobalNodeStore(UnsafeSlabArena<TNode> arena, RefCountTable<TNode> refCounts)
    {
        this.Arena = arena;
        this.RefCounts = refCounts;
        _nodeOps = default;
        _threadLocalBuffers = [];
        _remap = default;
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

    /// <summary>Per-worker thread-local buffers for this node type.</summary>
    public ThreadLocalBuffer<TNode>[] ThreadLocalBuffers => _threadLocalBuffers;

    /// <summary>
    /// Rewrites a local (tagged) handle to its corresponding global handle using the remap table
    /// populated by <see cref="DrainBuffers"/>. Global and <see cref="Handle{T}.None"/> handles are
    /// returned unchanged.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Handle<T> RemapHandle<T>(Handle<T> handle) where T : struct => _remap.Remap(handle);

    private RemapTable Remap => _remap;

    /// <summary>
    /// Sets the node operations struct. Used for two-phase initialization when the ops struct
    /// needs a reference back to this store.
    /// </summary>
    public void SetNodeOps(TNodeOps ops) => _nodeOps = ops;

    // ── Single-handle operations (used by visitor actions) ─────────────

    /// <summary>Increments the refcount for a single handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementRefCount(Handle<TNode> handle)
        => this.RefCounts.Increment(handle);

    /// <summary>
    /// Decrements the refcount for a single handle. If it reaches zero, cascades to free the node and its children.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecrementRefCount(Handle<TNode> handle)
    {
        if (!this.RefCounts.Decrement(handle))
            return;

        // Refcount hit zero: push onto the cascade pending stack. If no cascade is active, RunCascade
        // drains the stack. If a cascade is already active (re-entrant same-type child decrement, or
        // cross-store A→B→A bounce), the handle stays pending and this call returns — the outer loop
        // processes it; no nested cascade loops.
        _cascadePending.Push(handle);

        if (!_cascadeActive)
            this.RunCascade();
    }

    // ── Merge pipeline ─────────────────────────────────────────────────

    /// <summary>
    /// Drains all thread-local buffers into this store's arena, building the remap table.
    /// Returns the <c>(startIndex, count)</c> range of newly merged nodes.
    /// </summary>
    public (int StartIndex, int Count) DrainBuffers()
    {
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
        for (var threadIndex = 0; threadIndex < _threadLocalBuffers.Length; threadIndex++)
        {
            var threadLocalBuffer = _threadLocalBuffers[threadIndex];
            if (threadLocalBuffer.Count == 0)
                continue;

            var batchStart = this.Arena.AllocateBatch(threadLocalBuffer.Count);
            var span = threadLocalBuffer.WrittenSpan;
            for (var i = 0; i < span.Length; i++)
            {
                this.Arena[new Handle<TNode>(batchStart + i)] = span[i];
                _remap.SetMapping(threadIndex, i, batchStart + i);
            }
        }

        var count = this.Arena.HighWater - startIndex;
        this.RefCounts.EnsureCapacity(this.Arena.HighWater);
        _lastDrainStart = startIndex;
        _lastDrainCount = count;

        return (startIndex, count);
    }

    /// <summary>
    /// Rewrites local (tagged) handles to global indices and then increments child refcounts for
    /// the range produced by the most recent <see cref="DrainBuffers"/> call. Handle remapping uses
    /// this store's <typeparamref name="TNodeOps"/> (which implements <see cref="INodeVisitor.VisitRef{T}"/>
    /// to dispatch through each store's remap table). Child refcount incrementing uses
    /// <see cref="INodeOps{TNode}.IncrementChildRefCounts"/> on the same ops struct.
    ///
    /// BARRIER: all types must finish <see cref="DrainBuffers"/> before any type calls this method,
    /// because cross-type handle rewriting needs the target type's remap table.
    /// </summary>
    internal override void RewriteAndIncrementRefCounts()
    {
        this.AssertMergeActive();
        var startIndex = _lastDrainStart;
        var count = _lastDrainCount;

        var ops = _nodeOps;
        for (var i = 0; i < count; i++)
        {
            ref var node = ref this.Arena[new Handle<TNode>(startIndex + i)];
            ops.EnumerateRefChildren(ref node, ref ops);
        }

        for (var i = 0; i < count; i++)
        {
            ref readonly var node = ref this.Arena[new Handle<TNode>(startIndex + i)];
            _nodeOps.IncrementChildRefCounts(in node);
        }
    }

    /// <summary>
    /// Collects roots from thread-local buffers, increments their refcounts (the snapshot now owns them),
    /// and returns a <see cref="SnapshotSlice{TNode, TNodeOps}"/>. Combines phases 3 and 4 of the merge pipeline.
    /// </summary>
    public SnapshotSlice<TNode, TNodeOps> BuildSnapshotSlice()
    {
        this.AssertMergeActive();
        var roots = _rootListPool.TryPop(out var pooled) ? pooled : new UnsafeList<Handle<TNode>>();
        roots.Reset();
        this.CollectAndRemapRoots(roots);
        this.IncrementRoots(roots.WrittenSpan);
        return new SnapshotSlice<TNode, TNodeOps>(this, roots);
    }

    /// <summary>
    /// Collects root handles from all thread-local buffers into <paramref name="destination"/>,
    /// remapping local handles to global indices. Global handles pass through unchanged.
    /// </summary>
    private void CollectAndRemapRoots(UnsafeList<Handle<TNode>> destination)
    {
        foreach (var threadLocalBuffer in _threadLocalBuffers)
        {
            foreach (var handle in threadLocalBuffer.RootHandles)
                destination.Add(_remap.Remap(handle));
        }
    }

    internal override void Drain()
    {
        this.AssertMergeActive();
        this.DrainBuffers();
    }

    /// <summary>
    /// Resets all per-tick merge scratch state (thread-local buffers and remap table) so they
    /// are clean for the next tick. Backing arrays are retained for zero steady-state allocation.
    /// </summary>
    internal override void ResetMergeState()
    {
        this.AssertMergeActive();
        foreach (var threadLocalBuffer in _threadLocalBuffers)
            threadLocalBuffer.Reset();
        _remap.Reset();
    }

    // ── Root operations ──────────────────────────────────────────────────

    /// <summary>
    /// Releases a snapshot slice by decrementing root refcounts and cascading any that hit zero.
    /// This is the counterpart to <see cref="BuildSnapshotSlice"/>.
    /// </summary>
    public void ReleaseSnapshotSlice(SnapshotSlice<TNode, TNodeOps> slice)
    {
        this.DecrementRoots(slice.Roots);
        _rootListPool.Push(slice.DetachRoots());
    }

    private void IncrementRoots(ReadOnlySpan<Handle<TNode>> roots)
    {
        this.RefCounts.IncrementBatch(roots);
        this.TrackAndValidateAfterIncrement(roots);
    }

    private void DecrementRoots(ReadOnlySpan<Handle<TNode>> roots)
    {
        this.TrackBeforeDecrement(roots);
        Debug.Assert(!_cascadeActive, "DecrementRoots must not be called during an active cascade.");
        this.RefCounts.DecrementBatch(roots, _cascadePending);
        this.RunCascade();
        this.RunValidation();
    }

    // ── Cascade loop ─────────────────────────────────────────────────────

    private void RunCascade()
    {
        // CASCADE-FREE (owned entirely by this class): when a refcount reaches zero, this loop reads the
        // freed node from the arena, enumerates its children through TNodeOps (as both enumerator and
        // visitor), and frees the arena slot. Re-entrant decrements (same-type children) push onto
        // _cascadePending and the outer loop processes them — no nested loops, bounded stack depth.
        // Cross-type cascades trigger the other store's cascade via INodeVisitor.Visit dispatch.
        // The visitor pattern eliminates a separate cascade-free handler interface — TNodeOps handles it.
        // For cross-type DAGs, Visit{T} dispatch to the correct store often uses typeof checks (JIT may
        // eliminate dead branches).
        if (_cascadePending.Count == 0)
            return;

        _cascadeActive = true;

        var ops = _nodeOps;
        while (_cascadePending.TryPop(out var current))
        {
            ref readonly var node = ref this.Arena[current];
            ops.EnumerateChildren(in node, ref ops);
            this.Arena.Free(current);
        }

        _cascadeActive = false;
    }

    // ── Validation (debug only) ──────────────────────────────────────────

    /// <summary>
    /// Enables automatic DAG invariant checking (debug builds only). After every <see cref="IncrementRoots"/>
    /// and <see cref="DecrementRoots"/>, the store runs <see cref="DagValidator.AssertValid"/> in relaxed
    /// mode with the current set of tracked roots. Compiled out entirely in release builds.
    /// </summary>
    [Conditional("DEBUG")]
    private void EnableValidation()
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
            => new(arena, refCounts);

        [Conditional("DEBUG")]
        public void EnableValidation() => store.EnableValidation();

        public SnapshotSlice<TNode, TNodeOps> BuildSnapshotSlice(UnsafeList<Handle<TNode>> roots)
        {
            store.IncrementRoots(roots.WrittenSpan);
            return new SnapshotSlice<TNode, TNodeOps>(store, roots);
        }

        public void IncrementRoots(ReadOnlySpan<Handle<TNode>> roots) => store.IncrementRoots(roots);
        public void DecrementRoots(ReadOnlySpan<Handle<TNode>> roots) => store.DecrementRoots(roots);

        public void CollectAndRemapRoots(UnsafeList<Handle<TNode>> destination) => store.CollectAndRemapRoots(destination);
        public RemapTable Remap => store._remap;

        public UnsafeSlabArena<TNode> Arena => store.Arena;
        public RefCountTable<TNode> RefCounts => store.RefCounts;
        public TNodeOps NodeOps => store._nodeOps;
        public bool CascadeActive => store._cascadeActive;
    }
}
