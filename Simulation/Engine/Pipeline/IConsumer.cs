using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Consumes chain nodes on the consumption thread.
/// Constrained to struct for zero interface-dispatch overhead in the hot path.
///
/// Called once per frame by the consumption loop after rotating the previous/latest
/// pair and before advancing the epoch.  The consumer receives both nodes plus
/// save status so it can build whatever domain-specific frame representation it
/// needs (e.g. <see cref="RenderFrame"/> for simulation rendering).
///
/// LIFETIME: <paramref name="previous"/> and <paramref name="latest"/> are only
/// valid for the duration of this call.  The consumption loop advances the epoch
/// immediately after, so the production loop may free earlier nodes.
/// </summary>
internal interface IConsumer<TNode> where TNode : ChainNode<TNode>
{
    void Consume(
        TNode? previous,
        TNode latest,
        long frameStartNanoseconds,
        SaveStatus saveStatus,
        CancellationToken cancellationToken);
}
