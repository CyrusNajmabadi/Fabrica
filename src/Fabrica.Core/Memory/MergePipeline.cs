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
        var startIndex = arena.HighWater;

        for (var t = 0; t < tlbs.Length; t++)
        {
            var tlb = tlbs[t];
            if (tlb.Count == 0)
                continue;

            var batchStart = arena.AllocateBatch(tlb.Count);
            var span = tlb.WrittenSpan;
            for (var i = 0; i < span.Length; i++)
            {
                arena[new Handle<TNode>(batchStart + i)] = span[i];
                remap.SetMapping(t, i, batchStart + i);
            }
        }

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
        for (var i = 0; i < count; i++)
        {
            ref readonly var node = ref arena[new Handle<TNode>(startIndex + i)];
            nodeOps.EnumerateChildren(in node, ref visitor);
        }
    }

    /// <summary>
    /// Collects root handles from all TLBs for one node type, remapping local handles to global indices
    /// via <paramref name="remap"/>. Handles that are already global (e.g., references to pre-existing
    /// nodes from a prior snapshot) pass through unchanged.
    /// </summary>
    public static Handle<TNode>[] CollectAndRemapRoots<TNode>(
        ThreadLocalBuffer<TNode>[] tlbs,
        RemapTable remap)
        where TNode : struct
    {
        var roots = new List<Handle<TNode>>();
        foreach (var tlb in tlbs)
        {
            foreach (var handle in tlb.RootHandles)
            {
                var index = handle.Index;
                if (TaggedHandle.IsLocal(index))
                {
                    var threadId = TaggedHandle.DecodeThreadId(index);
                    var localIndex = TaggedHandle.DecodeLocalIndex(index);
                    roots.Add(new Handle<TNode>(remap.Resolve(threadId, localIndex)));
                }
                else
                {
                    roots.Add(handle);
                }
            }
        }

        return [.. roots];
    }
}
