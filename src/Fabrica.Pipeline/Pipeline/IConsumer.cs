namespace Fabrica.Pipeline;

/// <summary>
/// Consumes chain nodes on the consumption thread. Constrained to struct for zero interface-dispatch overhead in the hot path.
///
/// Called once per frame by the consumption loop after rotating the previous/latest pair and before advancing the epoch. The
/// consumer receives both nodes so it can build whatever domain-specific frame representation it needs (e.g. a render frame for
/// simulation rendering with interpolation and chain access).
/// </summary>
public interface IConsumer<TPayload>
{
    /// <summary>
    /// Process a frame using the current chain state.
    ///
    /// <para> <paramref name="previous"/> and <paramref name="latest"/> are always distinct, non-null object references. The
    /// consumption loop waits until two distinct nodes have been published before calling this method, so the consumer always has
    /// a valid interpolation range. The entire forward-linked chain from <paramref name="previous"/> to <paramref name="latest"/>
    /// is alive for the duration of this call.</para>
    ///
    /// <para> <paramref name="frameStartNanoseconds"/> is the wall-clock timestamp (in nanoseconds) sampled at the beginning of
    /// the current consumption frame, before any per-frame work. It is always ≥ <c>latest.PublishTimeNanoseconds</c> because the
    /// consumption thread reads already-published nodes. The gap (<c>frameStartNanoseconds - latest.PublishTimeNanoseconds</c>)
    /// tells the consumer how far past the latest simulation tick real time has advanced, which is useful for interpolation or
    /// timing per-frame work.</para>
    ///
    /// <para> LIFETIME: both nodes are only valid for the duration of this call. The consumption loop advances the epoch
    /// immediately after, so the production loop may free earlier nodes.</para>
    /// </summary>
    void Consume(
        BaseProductionLoop<TPayload>.ChainNode previous,
        BaseProductionLoop<TPayload>.ChainNode latest,
        long frameStartNanoseconds,
        CancellationToken cancellationToken);
}
