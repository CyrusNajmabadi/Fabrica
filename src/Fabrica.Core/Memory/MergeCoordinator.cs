namespace Fabrica.Core.Memory;

/// <summary>
/// Orchestrates the merge pipeline across all registered stores: drain thread-local buffers,
/// rewrite local handles to global indices, and increment child refcounts.
///
/// PIPELINE PHASES (managed via <see cref="MergeAll"/> and <see cref="MergeTransaction"/>)
///   1. <b>Drain</b> — copy each store's TLBs into its arena, building remap tables.
///      All drains must complete before any rewrite begins (cross-type remap dependency).
///   2. <b>Rewrite + refcount</b> — for each store, rewrite local handles via remap tables,
///      then increment child refcounts via <see cref="Nodes.INodeOps{TNode}.IncrementChildRefCounts"/>.
///   3. <i>(caller)</i> <b>Build slices</b> — per-store, type-specific. Only valid within the
///      returned <see cref="MergeTransaction"/> scope.
///   4. <b>Reset</b> — automatic on <see cref="MergeTransaction.Dispose"/>; clears TLBs and remap
///      tables for the next tick.
/// </summary>
public readonly struct MergeCoordinator(GlobalNodeStore[] stores)
{
    private readonly GlobalNodeStore[] _stores = stores;

    /// <summary>
    /// Executes phases 1 and 2 of the merge pipeline: drains all TLBs, then rewrites handles
    /// and increments child refcounts for every store. Returns a <see cref="MergeTransaction"/>
    /// that must be disposed to reset merge state. Snapshot slices may only be built within the
    /// transaction scope.
    /// </summary>
    public MergeTransaction MergeAll()
    {
        foreach (var store in _stores)
            store.Drain();

        foreach (var store in _stores)
            store.RewriteAndIncrementRefCounts();

        foreach (var store in _stores)
            store.IsMergeActive = true;

        return new MergeTransaction(_stores);
    }

    /// <summary>
    /// Scoped handle returned by <see cref="MergeAll"/>. While alive, stores accept
    /// <see cref="GlobalNodeStore{TNode,TNodeOps}.BuildSnapshotSlice"/> calls. Disposing resets
    /// all merge state (TLBs, remap tables) for the next tick.
    /// </summary>
    public struct MergeTransaction(GlobalNodeStore[] stores) : IDisposable
    {
        public readonly void Dispose()
        {
            foreach (var store in stores)
            {
                store.IsMergeActive = false;
                store.ResetMergeState();
            }
        }
    }
}
