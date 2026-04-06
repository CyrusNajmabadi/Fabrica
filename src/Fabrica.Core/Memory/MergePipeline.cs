namespace Fabrica.Core.Memory;

/// <summary>
/// Static helpers for the coordinator merge pipeline. Each tick, worker threads append nodes into per-worker
/// <see cref="ThreadLocalBuffer{T}"/> instances. After all jobs complete (the join barrier), the coordinator
/// calls these methods in order to drain TLBs into the global arena:
///
///   1. <see cref="DrainBuffers{TNode}"/>         — batch-allocate global slots, copy node data, build remap table.
///   2. <see cref="RewriteHandles{TNode,TNodeOps,TVisitor}"/>  — rewrite local handles to global using the remap.
///   3. <see cref="IncrementChildRefCounts{TNode,TNodeOps,TVisitor}"/> — increment refcounts for all children.
///   4. <see cref="CollectAndRemapRoots{TNode}"/> — gather root handles from TLBs, remap to global indices.
///
/// All methods are single-threaded. The coordinator runs them after the join barrier and before publishing
/// the next snapshot. In the future, steps 1-3 can run in parallel *per type* (with a barrier between 1 and 2
/// to ensure all remap tables are built before any cross-type handle rewriting).
/// </summary>
internal static class MergePipeline
{
    /// <summary>
    /// Drains all <see cref="ThreadLocalBuffer{TNode}"/> instances into the global arena. For each non-empty
    /// TLB: batch-allocates a contiguous range in the arena, copies the node data, and records the
    /// local-to-global mapping in <paramref name="remap"/>. Returns the <c>(startIndex, count)</c> range
    /// of newly merged nodes.
    /// </summary>
    public static (int StartIndex, int Count) DrainBuffers<TNode>(
        UnsafeSlabArena<TNode> arena,
        RefCountTable<TNode> refCounts,
        ThreadLocalBuffer<TNode>[] tlbs,
        RemapTable remap)
        where TNode : struct
    {
        // Remember where the arena was before we started so we can report the range of newly merged
        // nodes to the caller (they need this to know which nodes to fixup and refcount).
        var startIndex = arena.HighWater;

        // Each worker thread wrote nodes into its own TLB during the parallel work phase. Those nodes
        // still carry local handles (tagged with threadId + localIndex) that are only meaningful within
        // the originating TLB. We need to move them into the single shared arena so that every handle
        // in the system can be resolved to a global arena slot.
        //
        // For each thread's TLB:
        //   1. Reserve a contiguous block of global slots in the arena sized to fit this TLB's output.
        //   2. Copy the raw node data from the TLB into those slots.
        //   3. Record the local→global mapping (thread t, local index i → global batchStart+i) in the
        //      remap table so that RewriteHandles can later translate tagged local handles into their
        //      final global indices.
        for (var t = 0; t < tlbs.Length; t++)
        {
            var tlb = tlbs[t];
            if (tlb.Count == 0)
                continue;

            // Reserve tlb.Count contiguous slots starting at batchStart. This is O(1) — just a
            // high-water bump — and guarantees the slots are contiguous for cache-friendly iteration
            // in later phases.
            var batchStart = arena.AllocateBatch(tlb.Count);
            var span = tlb.WrittenSpan;
            for (var i = 0; i < span.Length; i++)
            {
                // Copy node data into its global slot and record where it landed so the remap table
                // can translate any local handle pointing to (thread=t, index=i) into batchStart+i.
                arena[new Handle<TNode>(batchStart + i)] = span[i];
                remap.SetMapping(t, i, batchStart + i);
            }
        }

        // The refcount table must be large enough to cover all the new global indices we just created,
        // otherwise IncrementChildRefCounts would write out of bounds.
        var count = arena.HighWater - startIndex;
        refCounts.EnsureCapacity(arena.HighWater);

        return (startIndex, count);
    }

    /// <summary>
    /// Rewrites local (tagged) handles to global indices in all nodes within the range
    /// <c>[startIndex, startIndex + count)</c>. Uses
    /// <see cref="INodeOps{TNode}.EnumerateRefChildren{TVisitor}"/> with a ref-mutation visitor
    /// (typically <c>RemapVisitor</c>) that resolves local handles through the remap tables.
    ///
    /// BARRIER: all types must finish <see cref="DrainBuffers{TNode}"/> before any type starts this
    /// step, because cross-type handle rewriting needs the target type's remap table.
    /// </summary>
    public static void RewriteHandles<TNode, TNodeOps, TVisitor>(
        UnsafeSlabArena<TNode> arena,
        int startIndex,
        int count,
        ref TNodeOps nodeOps,
        ref TVisitor visitor)
        where TNode : struct
        where TNodeOps : struct, INodeOps<TNode>
        where TVisitor : struct, INodeVisitor
    {
        // Walk every newly merged node in the arena. At this point the nodes have been copied verbatim
        // from TLBs, so any Handle<T> fields still contain tagged local values (threadId + localIndex).
        // EnumerateRefChildren hands each handle field *by reference* to the visitor, which resolves
        // the local handle through the appropriate remap table and overwrites it in place with the
        // global arena index. After this loop every handle field in the merged range points to a valid
        // global slot.
        for (var i = 0; i < count; i++)
        {
            ref var node = ref arena[new Handle<TNode>(startIndex + i)];
            nodeOps.EnumerateRefChildren(ref node, ref visitor);
        }
    }

    /// <summary>
    /// Increments child refcounts for all nodes within the range <c>[startIndex, startIndex + count)</c>.
    /// Uses <see cref="INodeOps{TNode}.EnumerateChildren{TVisitor}"/> with a read-only visitor
    /// (typically <c>RefcountVisitor</c>) that calls <see cref="RefCountTable{T}.Increment"/> for each child.
    /// </summary>
    public static void IncrementChildRefCounts<TNode, TNodeOps, TVisitor>(
        UnsafeSlabArena<TNode> arena,
        int startIndex,
        int count,
        ref TNodeOps nodeOps,
        ref TVisitor visitor)
        where TNode : struct
        where TNodeOps : struct, INodeOps<TNode>
        where TVisitor : struct, INodeVisitor
    {
        // Walk every newly merged node (handles are now global after RewriteHandles). For each node,
        // EnumerateChildren yields each child handle *by value* to the visitor, which increments that
        // child's refcount in the appropriate RefCountTable. This establishes the structural refcount
        // invariant: every node's RC equals the number of parent fields pointing to it. Root nodes
        // are not yet counted — they get their +1 from IncrementRoots after CollectAndRemapRoots.
        for (var i = 0; i < count; i++)
        {
            ref readonly var node = ref arena[new Handle<TNode>(startIndex + i)];
            nodeOps.EnumerateChildren(in node, ref visitor);
        }
    }

    /// <summary>
    /// Collects root handles from all TLBs for one node type into the caller-provided
    /// <paramref name="destination"/> list, remapping local handles to global indices via
    /// <paramref name="remap"/>. Handles that are already global (e.g., references to pre-existing
    /// nodes from a prior snapshot) pass through unchanged.
    ///
    /// The caller owns the <see cref="UnsafeList{T}"/> and is responsible for resetting it between
    /// ticks. In steady state this is zero-allocation: the list's backing array grows to the
    /// high-water root count and is reused across ticks.
    /// </summary>
    public static void CollectAndRemapRoots<TNode>(
        ThreadLocalBuffer<TNode>[] tlbs,
        RemapTable remap,
        UnsafeList<Handle<TNode>> destination)
        where TNode : struct
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
}
