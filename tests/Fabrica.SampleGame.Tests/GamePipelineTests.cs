using Fabrica.Core.Collections.Unsafe;
using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.SampleGame.Jobs;
using Fabrica.SampleGame.Nodes;
using Xunit;

namespace Fabrica.SampleGame.Tests;

/// <summary>
/// End-to-end integration test: creates all three stores, runs the 3-job DAG on real worker
/// threads, runs the full merge pipeline, validates with <see cref="DagValidator"/>, and verifies
/// cascade-free cleanup empties all arenas.
/// </summary>
public class GamePipelineTests : IDisposable
{
    private const int BackgroundWorkerCount = 4;
    private const int ItemCount = 4;
    private const int ChainLength = 4;

    private const int MachineTypeId = 0;
    private const int BeltTypeId = 1;
    private const int ItemTypeId = 2;

    private readonly WorkerPool _pool = new(workerCount: BackgroundWorkerCount, coordinatorCount: 1);

    public void Dispose() => _pool.Dispose();

    private (
        GlobalNodeStore<MachineNode, GameNodeOps> MachineStore,
        GlobalNodeStore<BeltSegmentNode, GameNodeOps> BeltStore,
        GlobalNodeStore<ItemNode, GameNodeOps> ItemStore)
        CreateStores()
    {
        var workerCount = _pool.WorkerCount;
        var machineStore = new GlobalNodeStore<MachineNode, GameNodeOps>(workerCount);
        var beltStore = new GlobalNodeStore<BeltSegmentNode, GameNodeOps>(workerCount);
        var itemStore = new GlobalNodeStore<ItemNode, GameNodeOps>(workerCount);

        var ops = new GameNodeOps
        {
            MachineStore = machineStore,
            BeltStore = beltStore,
            ItemStore = itemStore,
        };
        machineStore.SetNodeOps(ops);
        beltStore.SetNodeOps(ops);
        itemStore.SetNodeOps(ops);

        return (machineStore, beltStore, itemStore);
    }

    // ── DagValidator accessor ────────────────────────────────────────────

    private struct GameWorldAccessor(
        GlobalNodeStore<MachineNode, GameNodeOps> machineStore,
        GlobalNodeStore<BeltSegmentNode, GameNodeOps> beltStore,
        GlobalNodeStore<ItemNode, GameNodeOps> itemStore) : DagValidator.IWorldAccessor
    {
        public readonly int TypeCount => 3;

        public readonly int HighWater(int typeId) => typeId switch
        {
            MachineTypeId => machineStore.Arena.HighWater,
            BeltTypeId => beltStore.Arena.HighWater,
            ItemTypeId => itemStore.Arena.HighWater,
            _ => 0,
        };

        public readonly int GetRefCount(int typeId, int index) => typeId switch
        {
            MachineTypeId => machineStore.RefCounts.GetCount(new Handle<MachineNode>(index)),
            BeltTypeId => beltStore.RefCounts.GetCount(new Handle<BeltSegmentNode>(index)),
            ItemTypeId => itemStore.RefCounts.GetCount(new Handle<ItemNode>(index)),
            _ => 0,
        };

        public readonly void GetChildren(int typeId, int index, List<DagValidator.NodeRef> children)
        {
            switch (typeId)
            {
                case MachineTypeId:
                    {
                        ref readonly var node = ref machineStore.Arena[new Handle<MachineNode>(index)];
                        if (node.InputBelt.IsValid) children.Add(new DagValidator.NodeRef(BeltTypeId, node.InputBelt.Index));
                        if (node.OutputBelt.IsValid) children.Add(new DagValidator.NodeRef(BeltTypeId, node.OutputBelt.Index));
                        break;
                    }
                case BeltTypeId:
                    {
                        ref readonly var node = ref beltStore.Arena[new Handle<BeltSegmentNode>(index)];
                        if (node.Next.IsValid) children.Add(new DagValidator.NodeRef(BeltTypeId, node.Next.Index));
                        if (node.Payload.IsValid) children.Add(new DagValidator.NodeRef(ItemTypeId, node.Payload.Index));
                        break;
                    }
            }
        }
    }

    // ── Tests ────────────────────────────────────────────────────────────

    [Fact]
    public void FullPipeline_JobDag_Merge_Validate_Cleanup()
    {
        var (machineStore, beltStore, itemStore) = this.CreateStores();
        var scheduler = new JobScheduler(_pool);
        var schedulerAccessor = scheduler.GetTestAccessor();

        // ── Build and execute the job DAG ────────────────────────────────
        // Dependencies wired automatically via DependsOn in property setters.
        var spawnJob = new SpawnItemsJob(scheduler) { ItemThreadLocalBuffers = itemStore.ThreadLocalBuffers, Count = ItemCount };
        var beltJob = new BuildBeltChainJob(scheduler)
        {
            BeltThreadLocalBuffers = beltStore.ThreadLocalBuffers,
            SpawnJob = spawnJob,
            ChainLength = ChainLength,
        };
        _ = new PlaceMachinesJob(scheduler) { MachineThreadLocalBuffers = machineStore.ThreadLocalBuffers, BeltJob = beltJob };

        schedulerAccessor.Submit(spawnJob);

        // ── Merge pipeline (drain TLBs, rewrite handles, increment child refcounts) ──

        var coordinator = new MergeCoordinator([machineStore, beltStore, itemStore]);
        NonCopyableUnsafeList<Handle<MachineNode>> machineRoots;
        using (var merge = coordinator.MergeAll())
        {
            machineRoots = NonCopyableUnsafeList<Handle<MachineNode>>.Create();
            machineStore.GetTestAccessor().CollectAndRemapRoots(ref machineRoots);
            Assert.Equal(1, machineRoots.Count);

            machineStore.GetTestAccessor().IncrementRoots(machineRoots.WrittenSpan);
        }

        // ── Validate the DAG ─────────────────────────────────────────────

        var rootRef = new DagValidator.NodeRef(MachineTypeId, machineRoots[0].Index);
        var accessor = new GameWorldAccessor(machineStore, beltStore, itemStore);
        DagValidator.AssertValid([rootRef], accessor, strict: true);

        // ── Verify node structure ────────────────────────────────────────

        // Machine root should have RC = 1 (from root increment).
        var machineHandle = machineRoots[0];
        Assert.Equal(1, machineStore.RefCounts.GetCount(machineHandle));

        // Machine's InputBelt and OutputBelt should be valid global handles.
        ref readonly var machine = ref machineStore.Arena[machineHandle];
        Assert.True(machine.InputBelt.IsValid);
        Assert.True(machine.OutputBelt.IsValid);

        // Walk the belt chain from head — should have ChainLength segments.
        var current = machine.InputBelt;
        var segmentCount = 0;
        while (current.IsValid)
        {
            segmentCount++;
            ref readonly var seg = ref beltStore.Arena[current];
            current = seg.Next;
        }
        Assert.Equal(ChainLength, segmentCount);

        // ── Cascade-free cleanup ─────────────────────────────────────────

        machineStore.GetTestAccessor().DecrementRoots(machineRoots.WrittenSpan);

        // All arenas should be empty (high water stays, but all slots freed).
        // Verify refcounts are zero for all allocated nodes (skip index 0: None sentinel).
        for (var i = 1; i < machineStore.Arena.HighWater; i++)
            Assert.Equal(0, machineStore.RefCounts.GetCount(new Handle<MachineNode>(i)));
        for (var i = 1; i < beltStore.Arena.HighWater; i++)
            Assert.Equal(0, beltStore.RefCounts.GetCount(new Handle<BeltSegmentNode>(i)));
        for (var i = 1; i < itemStore.Arena.HighWater; i++)
            Assert.Equal(0, itemStore.RefCounts.GetCount(new Handle<ItemNode>(i)));
    }
}
