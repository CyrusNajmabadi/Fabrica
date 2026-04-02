namespace Fabrica.Pipeline;

/// <summary>
/// Callback for <see cref="SlabList{TPayload}.ProducerCleanupReleasedEntries{THandler}"/>. Called once for each entry being
/// reclaimed from the slab list. The handler should inspect the entry and take appropriate action — typically checking whether the
/// entry is pinned (via <see cref="PinnedVersions"/>) and either copying it to a side table for deferred processing, or releasing
/// its payload resources.
///
/// After the handler returns, the slab slot is cleared (<c>= default</c>) regardless of what the handler did. If the entry is
/// pinned, the handler must copy it before returning.
///
/// Constrained to struct for zero interface-dispatch overhead — the JIT specializes each call through the generic constraint.
/// </summary>
public interface IEntryCleanupHandler<TPayload>
{
    void HandleEntry(long position, in PipelineEntry<TPayload> entry);
}
