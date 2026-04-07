namespace Fabrica.Core.Memory;

/// <summary>
/// Non-generic merge-pipeline interface implemented by <see cref="GlobalNodeStore{TNode,TNodeOps}"/>.
/// Enables <see cref="MergeCoordinator"/> to orchestrate drain and reset phases across multiple
/// stores without knowing each store's node type.
/// </summary>
public interface IMergeParticipant
{
    /// <summary>Drains thread-local buffers into the global arena and builds the remap table.</summary>
    void Drain();

    /// <summary>Resets thread-local buffers and the remap table for the next tick.</summary>
    void ResetMergeState();
}
