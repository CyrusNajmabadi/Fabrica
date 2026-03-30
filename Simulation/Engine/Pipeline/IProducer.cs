using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Produces new chain nodes on the production thread.
/// Constrained to struct for zero interface-dispatch overhead in the hot path.
///
/// The production loop owns the node pool and chain mechanics.  The producer
/// is responsible only for domain-specific work: filling payloads, coordinating
/// workers, and releasing payload resources when nodes are freed.
///
/// Lifecycle per tick:
///   1. Loop rents a node and calls <see cref="ChainNode{TSelf}.InitializeBase"/>.
///   2. Loop calls <see cref="Produce"/> — the producer fills the node's payload.
///   3. Loop links the node onto the chain and publishes it.
///
/// Lifecycle on cleanup:
///   Loop calls <see cref="ReleaseResources"/> before returning the node to the pool.
/// </summary>
internal interface IProducer<TNode> where TNode : ChainNode<TNode>
{
    /// <summary>
    /// Fill the initial (sequence-0) node.  Called once at startup on the
    /// production thread before the first tick.
    /// </summary>
    void Bootstrap(TNode node, CancellationToken cancellationToken);

    /// <summary>
    /// Fill <paramref name="next"/> using data from <paramref name="current"/>.
    /// Called once per tick on the production thread.
    /// </summary>
    void Produce(TNode current, TNode next, CancellationToken cancellationToken);

    /// <summary>
    /// Release domain-specific resources from a node that is being freed
    /// (e.g. return a WorldImage to its pool).  Called on the production thread
    /// before the node is returned to the node pool.
    /// </summary>
    void ReleaseResources(TNode node);
}
