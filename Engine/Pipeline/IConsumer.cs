namespace Engine.Pipeline;

/// <summary>
/// Consumes chain nodes on the consumption thread.
/// Constrained to struct for zero interface-dispatch overhead in the hot path.
///
/// Called once per frame by the consumption loop after rotating the previous/latest
/// pair and before advancing the epoch.  The consumer receives both nodes so it can
/// build whatever domain-specific frame representation it needs (e.g. a render frame
/// for simulation rendering with interpolation and chain access).
/// </summary>
internal interface IConsumer<TPayload>
{
    /// <summary>
    /// Process a frame using the current chain state.
    ///
    /// <para><paramref name="previous"/> and <paramref name="latest"/> are always
    /// distinct, non-null object references.  The consumption loop waits until two
    /// distinct nodes have been published before calling this method, so the consumer
    /// always has a valid interpolation range.  The entire forward-linked chain from
    /// <paramref name="previous"/> to <paramref name="latest"/> is alive for the
    /// duration of this call.</para>
    ///
    /// <para><paramref name="frameStartNanoseconds"/> is the wall-clock timestamp
    /// (in nanoseconds) sampled at the beginning of the current consumption frame,
    /// before any per-frame work.  Consumers can use it for interpolation (e.g.
    /// comparing against <c>latest.PublishTimeNanoseconds</c>) or for timing their
    /// own work.</para>
    ///
    /// <para>LIFETIME: both nodes are only valid for the duration of this call.
    /// The consumption loop advances the epoch immediately after, so the production
    /// loop may free earlier nodes.</para>
    /// </summary>
    void Consume(
        BaseProductionLoop<TPayload>.ChainNode previous,
        BaseProductionLoop<TPayload>.ChainNode latest,
        long frameStartNanoseconds,
        CancellationToken cancellationToken);
}
