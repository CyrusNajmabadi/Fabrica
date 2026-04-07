using Fabrica.Core.Memory;
using Fabrica.Game.Nodes;

namespace Fabrica.Game;

/// <summary>
/// Holds the per-tick mutable state shared across the producer and its jobs: node stores
/// and root collection lists. Created once by <see cref="GameEngine"/> and reused across ticks.
/// </summary>
internal sealed class GameTickState
{
    internal readonly GlobalNodeStore<MachineNode, GameNodeOps> MachineStore;
    internal readonly GlobalNodeStore<BeltSegmentNode, GameNodeOps> BeltStore;
    internal readonly GlobalNodeStore<ItemNode, GameNodeOps> ItemStore;

    internal readonly UnsafeList<Handle<MachineNode>> MachineRoots = new();
    internal readonly UnsafeList<Handle<BeltSegmentNode>> BeltRoots = new();
    internal readonly UnsafeList<Handle<ItemNode>> ItemRoots = new();

    internal GameTickState(int workerCount)
    {
        MachineStore = new(workerCount);
        BeltStore = new(workerCount);
        ItemStore = new(workerCount);

        var ops = new GameNodeOps
        {
            MachineStore = MachineStore,
            BeltStore = BeltStore,
            ItemStore = ItemStore,
        };
        MachineStore.SetNodeOps(ops);
        BeltStore.SetNodeOps(ops);
        ItemStore.SetNodeOps(ops);
    }

    /// <summary>
    /// Resets all per-tick scratch state (thread-local buffers, remap tables, root lists)
    /// so they are clean for the next tick. Backing arrays are retained for zero steady-state
    /// allocation.
    /// </summary>
    internal void Reset()
    {
        MachineStore.ResetMergeState();
        BeltStore.ResetMergeState();
        ItemStore.ResetMergeState();

        MachineRoots.Reset();
        BeltRoots.Reset();
        ItemRoots.Reset();
    }
}
