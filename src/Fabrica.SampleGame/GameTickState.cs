using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.SampleGame.Jobs;
using Fabrica.SampleGame.Nodes;

namespace Fabrica.SampleGame;

/// <summary>
/// Holds the per-tick mutable state shared across the producer and its jobs: node stores,
/// merge coordinator, and pre-allocated job instances. Created once by <see cref="GameEngine"/>
/// and reused across ticks.
/// </summary>
internal sealed class GameTickState
{
    internal readonly GlobalNodeStore<MachineNode, GameNodeOps> MachineStore;
    internal readonly GlobalNodeStore<BeltSegmentNode, GameNodeOps> BeltStore;
    internal readonly GlobalNodeStore<ItemNode, GameNodeOps> ItemStore;
    internal readonly MergeCoordinator Coordinator;

    internal readonly SpawnItemsJob SpawnJob;
    internal readonly BuildBeltChainJob BeltJob;
    internal readonly PlaceMachinesJob MachineJob;

    internal GameTickState(int workerCount, JobScheduler scheduler)
    {
        SpawnJob = new SpawnItemsJob(scheduler);
        BeltJob = new BuildBeltChainJob(scheduler);
        MachineJob = new PlaceMachinesJob(scheduler);
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

        Coordinator = new MergeCoordinator([MachineStore, BeltStore, ItemStore]);
    }
}
