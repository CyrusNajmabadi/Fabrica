using Fabrica.Core.Memory;
using Fabrica.Game.Nodes;

namespace Fabrica.Game;

/// <summary>
/// Holds the per-tick mutable state shared across the producer and its jobs: node stores,
/// thread-local buffers, remap tables, and root collection lists. Created once by
/// <see cref="GameEngine"/> and reused across ticks.
/// </summary>
internal sealed class GameTickState
{
    internal readonly GlobalNodeStore<MachineNode, GameNodeOps> MachineStore;
    internal readonly GlobalNodeStore<BeltSegmentNode, GameNodeOps> BeltStore;
    internal readonly GlobalNodeStore<ItemNode, GameNodeOps> ItemStore;

    internal readonly ThreadLocalBuffer<MachineNode>[] MachineTlbs;
    internal readonly ThreadLocalBuffer<BeltSegmentNode>[] BeltTlbs;
    internal readonly ThreadLocalBuffer<ItemNode>[] ItemTlbs;

    internal readonly RemapTable MachineRemap;
    internal readonly RemapTable BeltRemap;
    internal readonly RemapTable ItemRemap;

    internal readonly UnsafeList<Handle<MachineNode>> MachineRoots = new();

    internal GameTickState(int workerCount)
    {
        MachineStore = new();
        BeltStore = new();
        ItemStore = new();

        MachineTlbs = new ThreadLocalBuffer<MachineNode>[workerCount];
        BeltTlbs = new ThreadLocalBuffer<BeltSegmentNode>[workerCount];
        ItemTlbs = new ThreadLocalBuffer<ItemNode>[workerCount];

        for (var i = 0; i < workerCount; i++)
        {
            MachineTlbs[i] = new ThreadLocalBuffer<MachineNode>(i);
            BeltTlbs[i] = new ThreadLocalBuffer<BeltSegmentNode>(i);
            ItemTlbs[i] = new ThreadLocalBuffer<ItemNode>(i);
        }

        MachineRemap = new RemapTable(workerCount);
        BeltRemap = new RemapTable(workerCount);
        ItemRemap = new RemapTable(workerCount);

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
}
