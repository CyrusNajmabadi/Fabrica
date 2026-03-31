using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Consumes chain nodes on the consumption thread.
/// Constrained to struct for zero interface-dispatch overhead in the hot path.
///
/// Called once per frame by the consumption loop after rotating the previous/latest
/// pair and before advancing the epoch.  The consumer receives both nodes so it can
/// build whatever domain-specific frame representation it needs (e.g. a render frame
/// for simulation rendering with interpolation and chain access).
///
/// LIFETIME: <paramref name="previous"/> and <paramref name="latest"/> are only
/// valid for the duration of this call.  The consumption loop advances the epoch
/// immediately after, so the production loop may free earlier nodes.
/// </summary>
internal interface IConsumer<TPayload>
{
    void Consume(
        ChainNode<TPayload>? previous,
        ChainNode<TPayload> latest,
        long frameStartNanoseconds,
        CancellationToken cancellationToken);
}
