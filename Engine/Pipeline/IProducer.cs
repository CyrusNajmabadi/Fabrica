namespace Engine.Pipeline;

/// <summary>
/// Produces new payloads on the production thread.
/// Constrained to struct for zero interface-dispatch overhead in the hot path.
///
/// The production loop owns the node pool and chain mechanics.  The producer
/// is responsible only for domain-specific work: creating payloads, coordinating
/// workers, and releasing payload resources when nodes are freed.
///
/// Lifecycle per tick:
///   1. Loop rents a node and calls InitializeBase.
///   2. Loop calls <see cref="Produce"/> — the producer creates a new payload.
///   3. Loop assigns the payload to the node, links, and publishes it.
///
/// Lifecycle on cleanup:
///   Loop calls <see cref="ReleaseResources"/> before returning the node to the pool.
/// </summary>
internal interface IProducer<TPayload>
{
    /// <summary>
    /// Create the initial (sequence-0) payload.  Called once at startup on the
    /// production thread before the first tick.
    /// </summary>
    TPayload CreateInitialPayload(CancellationToken cancellationToken);

    /// <summary>
    /// Create the next payload using data from <paramref name="current"/>.
    /// Called once per tick on the production thread.
    /// </summary>
    TPayload Produce(TPayload current, CancellationToken cancellationToken);

    /// <summary>
    /// Release domain-specific resources from a payload that is being freed
    /// (e.g. return a WorldImage to its pool).  Called on the production thread
    /// before the node is returned to the node pool.
    /// </summary>
    void ReleaseResources(TPayload payload);

    /// <summary>
    /// Shut down any resources owned by the producer (e.g. worker groups).
    /// Called once after the production loop exits.
    /// </summary>
    void Shutdown();
}
