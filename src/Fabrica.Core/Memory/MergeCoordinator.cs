namespace Fabrica.Core.Memory;

/// <summary>
/// Orchestrates the non-generic phases of the merge pipeline across all registered stores.
/// <see cref="DrainAll"/> runs phase 1 (TLB drain) for every store, establishing the barrier
/// that cross-type handle rewriting requires. <see cref="ResetAll"/> clears per-tick scratch state.
///
/// Type-specific phases (rewrite + refcount, root collection, slice building) remain on each
/// <see cref="GlobalNodeStore{TNode,TNodeOps}"/> because they need the concrete node and ops types.
/// </summary>
public sealed class MergeCoordinator(IMergeParticipant[] stores)
{
    /// <summary>
    /// Drains all stores' thread-local buffers into their arenas (merge phase 1). All drains
    /// must complete before any store runs rewrite-and-refcount, because cross-type handle
    /// rewriting depends on all remap tables being fully populated.
    /// </summary>
    public void DrainAll()
    {
        foreach (var store in stores)
            store.Drain();
    }

    /// <summary>
    /// Resets all stores' per-tick merge scratch (thread-local buffers and remap tables).
    /// </summary>
    public void ResetAll()
    {
        foreach (var store in stores)
            store.ResetMergeState();
    }
}
