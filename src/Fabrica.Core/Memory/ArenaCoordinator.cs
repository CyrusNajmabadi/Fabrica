using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fabrica.Core.Threading;

namespace Fabrica.Core.Memory;

/// <summary>
/// Merges per-thread <see cref="ThreadLocalBuffer{T}"/> contents into a shared <see cref="UnsafeSlabArena{T}"/> and
/// <see cref="RefCountTable"/>, then processes deferred refcount releases. Completes the arena-backed persistent data
/// structure pipeline.
///
/// MERGE PIPELINE (per buffer, via <see cref="MergeBuffer"/>)
///   1. Allocate global arena indices for every new node in the buffer.
///   2. Ensure <see cref="RefCountTable"/> capacity for the new index range.
///   3. Copy each node struct from the buffer to its global arena slot.
///   4. Call <see cref="IArenaNode.FixupReferences"/> to remap tagged local indices → global indices.
///   5. Call <see cref="IArenaNode.IncrementChildren"/> to establish refcounts for child references.
///
/// After all buffers are merged, <see cref="ProcessReleases{THandler}"/> collects and batch-decrements all
/// deferred release entries, triggering cascade-free via the <see cref="RefCountTable.IRefCountHandler"/>.
///
/// ZERO ALLOCATION
///   The local-to-global remap array (<see cref="_localToGlobalMap"/>) and release batch array
///   (<see cref="_releaseBatch"/>) are fields that grow as needed but never shrink. After warmup, the merge
///   pipeline allocates nothing.
///
/// THREAD MODEL
///   Single-threaded. Must be called from the coordinator thread at the fork-join boundary. Debug builds assert
///   via <see cref="SingleThreadedOwner"/>.
///
/// PORTABILITY
///   No GC reliance. All storage is value types in arrays. In Rust/C++: <c>Vec&lt;i32&gt;</c> for the remap
///   arrays, same arena + refcount table.
/// </summary>
internal sealed class ArenaCoordinator<TNode>(UnsafeSlabArena<TNode> arena, RefCountTable refCounts) where TNode : struct, IArenaNode
{
    private readonly UnsafeSlabArena<TNode> _arena = arena;
    private readonly RefCountTable _refCounts = refCounts;

    private int[] _localToGlobalMap = new int[256];
    private int _lastMapCount;

    private int[] _releaseBatch = new int[256];
    private int _releaseBatchCount;

    private SingleThreadedOwner _owner;

    /// <summary>The global arena this coordinator writes to.</summary>
    public UnsafeSlabArena<TNode> Arena => _arena;

    /// <summary>The refcount table this coordinator manages.</summary>
    public RefCountTable RefCounts => _refCounts;

    // ── Primary API ──────────────────────────────────────────────────────

    /// <summary>
    /// Merges a single buffer into the global arena: allocates global indices, copies nodes, fixes up local
    /// references, and increments child refcounts. After this call, <see cref="GetGlobalIndex"/> returns the
    /// global index for any local index in this buffer.
    ///
    /// The caller is responsible for incrementing refcounts of any "root" nodes (nodes that represent live
    /// references, such as the current tree version root) after this call returns.
    /// </summary>
    public void MergeBuffer(ThreadLocalBuffer<TNode> buffer)
    {
        _owner.AssertOwnerThread();

        var count = buffer.Count;
        _lastMapCount = count;
        if (count == 0)
            return;

        EnsureArrayCapacity(ref _localToGlobalMap, count);

        // Phase 1: Allocate global indices for all new nodes.
        var maxGlobalIndex = 0;
        for (var i = 0; i < count; i++)
        {
            var globalIndex = _arena.Allocate();
            _localToGlobalMap[i] = globalIndex;
            if (globalIndex > maxGlobalIndex)
                maxGlobalIndex = globalIndex;
        }

        // Phase 2: Ensure refcount table has capacity for the new high-water mark.
        _refCounts.EnsureCapacity(maxGlobalIndex + 1);

        // Phase 3: Copy, fixup, and increment children.
        var nodes = buffer.Nodes;
        var map = new ReadOnlySpan<int>(_localToGlobalMap, 0, count);

        for (var i = 0; i < count; i++)
        {
            var globalIndex = _localToGlobalMap[i];
            _arena[globalIndex] = nodes[i];
            _arena[globalIndex].FixupReferences(map);
            _arena[globalIndex].IncrementChildren(_refCounts);
        }
    }

    /// <summary>
    /// Returns the global arena index corresponding to a local buffer index from the most recent
    /// <see cref="MergeBuffer"/> call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetGlobalIndex(int localIndex)
    {
        Debug.Assert(localIndex >= 0 && localIndex < _lastMapCount,
            $"Local index {localIndex} is out of range [0, {_lastMapCount}).");
        return _localToGlobalMap[localIndex];
    }

    /// <summary>
    /// Collects all deferred release entries from the given buffers and batch-decrements their refcounts,
    /// triggering cascade-free via the handler. Must be called after all <see cref="MergeBuffer"/> calls
    /// so that child refcounts from new nodes are already established before old roots are released.
    /// </summary>
    public void ProcessReleases<THandler>(ThreadLocalBuffer<TNode>[] buffers, THandler handler)
        where THandler : struct, RefCountTable.IRefCountHandler
    {
        _owner.AssertOwnerThread();

        _releaseBatchCount = 0;
        for (var b = 0; b < buffers.Length; b++)
        {
            while (buffers[b].TryPopRelease(out var index))
            {
                EnsureArrayCapacity(ref _releaseBatch, _releaseBatchCount + 1);
                _releaseBatch[_releaseBatchCount++] = index;
            }
        }

        if (_releaseBatchCount > 0)
            _refCounts.DecrementBatch(
                new ReadOnlySpan<int>(_releaseBatch, 0, _releaseBatchCount), handler);
    }

    /// <summary>
    /// Convenience method: merges all buffers, then processes all releases. For callers that don't need to
    /// inspect per-buffer mappings between merges.
    /// </summary>
    public void Merge<THandler>(ThreadLocalBuffer<TNode>[] buffers, THandler handler)
        where THandler : struct, RefCountTable.IRefCountHandler
    {
        _owner.AssertOwnerThread();

        for (var b = 0; b < buffers.Length; b++)
            this.MergeBuffer(buffers[b]);

        this.ProcessReleases(buffers, handler);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnsureArrayCapacity(ref int[] array, int requiredLength)
    {
        if (array.Length >= requiredLength)
            return;

        GrowArray(ref array, requiredLength);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void GrowArray(ref int[] array, int requiredLength)
    {
        var newLength = array.Length;
        while (newLength < requiredLength)
            newLength *= 2;

        var newArray = new int[newLength];
        Array.Copy(array, newArray, array.Length);
        array = newArray;
    }

    // ── Test accessor ─────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor()
        => new(this);

    internal readonly struct TestAccessor(ArenaCoordinator<TNode> coordinator)
    {
        public int[] LocalToGlobalMap => coordinator._localToGlobalMap;
        public int LastMapCount => coordinator._lastMapCount;
        public int ReleaseBatchCount => coordinator._releaseBatchCount;
    }
}
