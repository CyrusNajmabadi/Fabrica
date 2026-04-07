using Fabrica.Core.Collections.Unsafe;
using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.Game.Jobs;
using Fabrica.Game.Nodes;
using Xunit;

namespace Fabrica.Game.Tests;

/// <summary>
/// End-to-end integration test: creates all three stores, runs the 3-job DAG on real worker
/// threads, runs the full merge pipeline, validates with <see cref="DagValidator"/>, and verifies
/// cascade-free cleanup empties all arenas.
/// </summary>
public class GamePipelineTests : IDisposable
{
    private const int WorkerCount = 4;
    private const int ItemCount = 4;
    private const int ChainLength = 4;

    private const int MachineTypeId = 0;
    private const int BeltTypeId = 1;
    private const int ItemTypeId = 2;

    private readonly WorkerPool _pool = new(workerCount: WorkerCount);

    public void Dispose() => _pool.Dispose();

    private static (
        GlobalNodeStore<MachineNode, GameNodeOps> MachineStore,
        GlobalNodeStore<BeltSegmentNode, GameNodeOps> BeltStore,
        GlobalNodeStore<ItemNode, GameNodeOps> ItemStore)
        CreateStores()
    {
        var machineStore = new GlobalNodeStore<MachineNode, GameNodeOps>(WorkerCount);
        var beltStore = new GlobalNodeStore<BeltSegmentNode, GameNodeOps>(WorkerCount);
        var itemStore = new GlobalNodeStore<ItemNode, GameNodeOps>(WorkerCount);

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
        var (machineStore, beltStore, itemStore) = CreateStores();
        var scheduler = new JobScheduler(_pool);
        var schedulerAccessor = scheduler.GetTestAccessor();

        // ── Build and execute the job DAG ────────────────────────────────
        // Dependencies wired automatically via DependsOn in property setters.
        var spawnJob = new SpawnItemsJob { ItemThreadLocalBuffers = itemStore.ThreadLocalBuffers, Count = ItemCount };
        var beltJob = new BuildBeltChainJob
        {
            BeltThreadLocalBuffers = beltStore.ThreadLocalBuffers,
            SpawnJob = spawnJob,
            ChainLength = ChainLength,
        };
        _ = new PlaceMachinesJob { MachineThreadLocalBuffers = machineStore.ThreadLocalBuffers, BeltJob = beltJob };

        schedulerAccessor.Submit(spawnJob);

        // ── Merge pipeline ───────────────────────────────────────────────

        // Phase 1: drain TLBs.
        var (itemStart, itemCount) = itemStore.DrainBuffers();
        var (beltStart, beltCount) = beltStore.DrainBuffers();
        var (machineStart, machineCount) = machineStore.DrainBuffers();

        Assert.Equal(ItemCount, itemCount);
        Assert.Equal(ChainLength, beltCount);
        Assert.Equal(1, machineCount);

        // Phase 2: rewrite handles and increment child refcounts.
        var refcountVisitor = new GameRefcountVisitor
        {
            MachineStore = machineStore,
            BeltStore = beltStore,
            ItemStore = itemStore,
        };

        itemStore.RewriteAndIncrementRefCounts(itemStart, itemCount, ref refcountVisitor);
        beltStore.RewriteAndIncrementRefCounts(beltStart, beltCount, ref refcountVisitor);
        machineStore.RewriteAndIncrementRefCounts(machineStart, machineCount, ref refcountVisitor);

        // Phase 3: collect and remap roots.
        var machineRoots = new UnsafeList<Handle<MachineNode>>();
        machineStore.CollectAndRemapRoots(machineRoots);
        Assert.Equal(1, machineRoots.Count);

        // Increment root refcounts.
        machineStore.GetTestAccessor().IncrementRoots(machineRoots.WrittenSpan);

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
        // Verify refcounts are zero for all allocated nodes.
        for (var i = 0; i < machineStore.Arena.HighWater; i++)
            Assert.Equal(0, machineStore.RefCounts.GetCount(new Handle<MachineNode>(i)));
        for (var i = 0; i < beltStore.Arena.HighWater; i++)
            Assert.Equal(0, beltStore.RefCounts.GetCount(new Handle<BeltSegmentNode>(i)));
        for (var i = 0; i < itemStore.Arena.HighWater; i++)
            Assert.Equal(0, itemStore.RefCounts.GetCount(new Handle<ItemNode>(i)));
    }
}
