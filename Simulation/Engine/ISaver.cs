using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Performs the actual (blocking) save operation for a chain node.
/// Implementations are constrained to struct for zero interface-dispatch overhead.
///
/// Called from the save task thread (threadpool), not the consumption thread.
/// The node is guaranteed pinned for the duration of this call — the production
/// loop will not reclaim it until the save runner's callback returns.
///
/// Separated from save dispatch (<see cref="ISaveRunner{TNode}"/>) so tests
/// can drive success and failure independently, without depending on thread-pool
/// behaviour.
/// </summary>
internal interface ISaver<TNode> where TNode : ChainNode<TNode>
{
    void Save(TNode node, int sequenceNumber);
}
