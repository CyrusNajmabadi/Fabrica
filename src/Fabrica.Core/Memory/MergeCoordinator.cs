namespace Fabrica.Core.Memory;

/// <summary>
/// Orchestrates the merge pipeline across all registered stores: drain thread-local buffers,
/// rewrite local handles to global indices, and increment child refcounts. After
/// <see cref="MergeAll"/> completes, callers can build snapshot slices from each typed store.
///
/// PIPELINE PHASES
///   1. <b>Drain</b> — copy each store's TLBs into its arena, building remap tables.
///      All drains must complete before any rewrite begins (cross-type remap dependency).
///   2. <b>Rewrite + refcount</b> — for each store, rewrite local handles via remap tables,
///      then increment child refcounts via <see cref="Nodes.INodeOps{TNode}.IncrementChildRefCounts"/>.
///   3. <i>(caller)</i> <b>Build slices</b> — per-store, type-specific.
///   4. <b>Reset</b> — clear TLBs and remap tables for the next tick.
/// </summary>
public readonly struct MergeCoordinator(GlobalNodeStore[] stores)
{
    private readonly GlobalNodeStore[] _stores = stores;

    /// <summary>
    /// Executes phases 1 and 2 of the merge pipeline: drains all TLBs, then rewrites handles
    /// and increments child refcounts for every store. After this returns, callers may call
    /// <see cref="GlobalNodeStore{TNode,TNodeOps}.BuildSnapshotSlice"/> on each typed store,
    /// then <see cref="ResetAll"/> to prepare for the next tick.
    /// </summary>
    public void MergeAll()
    {
        foreach (var store in _stores)
            store.Drain();

        foreach (var store in _stores)
            store.RewriteAndIncrementRefCounts();
    }

    /// <summary>
    /// Resets all stores' per-tick merge scratch (thread-local buffers and remap tables).
    /// </summary>
    public void ResetAll()
    {
        foreach (var store in _stores)
            store.ResetMergeState();
    }
}
