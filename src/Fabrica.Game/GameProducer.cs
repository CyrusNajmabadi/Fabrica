using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.Game.Jobs;
using Fabrica.Game.Nodes;
using Fabrica.Pipeline;

namespace Fabrica.Game;

/// <summary>
/// Produces one <see cref="GameWorldImage"/> per tick by building and executing a job DAG,
/// merging results into the global stores, and publishing a snapshot.
/// </summary>
public readonly struct GameProducer : IProducer<GameWorldImage>
{
    private readonly ObjectPool<GameWorldImage, GameWorldImage.Allocator> _imagePool = new(initialCapacity: 8);
    private readonly JobScheduler _scheduler;
    private readonly GameTickState _tickState;

    internal GameProducer(
        JobScheduler scheduler,
        GameTickState tickState)
    {
        _scheduler = scheduler;
        _tickState = tickState;
    }

    public GameWorldImage CreateInitialPayload(CancellationToken cancellationToken)
        => _imagePool.Rent();

    public GameWorldImage Produce(GameWorldImage current, CancellationToken cancellationToken)
    {
        // Each tick:
        //   1. Build the 3-job DAG (SpawnItems → BuildBelts → PlaceMachines).
        //   2. Submit via JobScheduler (blocks until the DAG completes).
        //   3. Run the merge pipeline: drain → rewrite + refcount → collect roots.
        //   4. Build SnapshotSlice instances and increment root refcounts.
        //   5. Reset per-worker buffers and remap tables for the next tick.

        var image = _imagePool.Rent();
        var tickState = _tickState;

        // ── 1. Build the job DAG ─────────────────────────────────────────
        // Dependencies are wired automatically by property setters (DependsOn).
        var spawnJob = new SpawnItemsJob { ItemThreadLocalBuffers = tickState.ItemThreadLocalBuffers, Count = 4 };
        var beltJob = new BuildBeltChainJob
        {
            BeltThreadLocalBuffers = tickState.BeltThreadLocalBuffers,
            SpawnJob = spawnJob,
            ChainLength = 4,
        };
        _ = new PlaceMachinesJob { MachineThreadLocalBuffers = tickState.MachineThreadLocalBuffers, BeltJob = beltJob };

        // ── 2. Execute ──────────────────────────────────────────────────
        _scheduler.Submit(spawnJob);

        // ── 3. Merge pipeline ───────────────────────────────────────────

        // Phase 1: drain all TLBs into global arenas and build remap tables.
        var (itemStart, itemCount) = tickState.ItemStore.DrainBuffers(tickState.ItemThreadLocalBuffers, tickState.ItemRemap);
        var (beltStart, beltCount) = tickState.BeltStore.DrainBuffers(tickState.BeltThreadLocalBuffers, tickState.BeltRemap);
        var (machineStart, machineCount) =
            tickState.MachineStore.DrainBuffers(tickState.MachineThreadLocalBuffers, tickState.MachineRemap);

        // Phase 2: rewrite local handles to global and increment child refcounts.
        var remapVisitor = new GameRemapVisitor
        {
            MachineRemap = tickState.MachineRemap,
            BeltRemap = tickState.BeltRemap,
            ItemRemap = tickState.ItemRemap,
        };
        var refcountVisitor = new GameRefcountVisitor
        {
            MachineStore = tickState.MachineStore,
            BeltStore = tickState.BeltStore,
            ItemStore = tickState.ItemStore,
        };

        tickState.ItemStore.RewriteAndIncrementRefCounts(itemStart, itemCount, ref remapVisitor, ref refcountVisitor);
        tickState.BeltStore.RewriteAndIncrementRefCounts(beltStart, beltCount, ref remapVisitor, ref refcountVisitor);
        tickState.MachineStore.RewriteAndIncrementRefCounts(machineStart, machineCount, ref remapVisitor, ref refcountVisitor);

        // Phase 3: collect and remap root handles for all types.
        tickState.MachineStore.CollectAndRemapRoots(
            tickState.MachineThreadLocalBuffers, tickState.MachineRemap, tickState.MachineRoots);
        tickState.BeltStore.CollectAndRemapRoots(
            tickState.BeltThreadLocalBuffers, tickState.BeltRemap, tickState.BeltRoots);
        tickState.ItemStore.CollectAndRemapRoots(
            tickState.ItemThreadLocalBuffers, tickState.ItemRemap, tickState.ItemRoots);

        // ── 4. Build snapshot slices ────────────────────────────────────
        var machineRootList = new UnsafeList<Handle<MachineNode>>();
        foreach (var root in tickState.MachineRoots.WrittenSpan)
            machineRootList.Add(root);

        var beltRootList = new UnsafeList<Handle<BeltSegmentNode>>();
        foreach (var root in tickState.BeltRoots.WrittenSpan)
            beltRootList.Add(root);

        var itemRootList = new UnsafeList<Handle<ItemNode>>();
        foreach (var root in tickState.ItemRoots.WrittenSpan)
            itemRootList.Add(root);

        image.MachineSlice = new SnapshotSlice<MachineNode, GameNodeOps>(tickState.MachineStore, machineRootList);
        image.MachineSlice.IncrementRootRefCounts();

        image.BeltSlice = new SnapshotSlice<BeltSegmentNode, GameNodeOps>(tickState.BeltStore, beltRootList);
        image.BeltSlice.IncrementRootRefCounts();

        image.ItemSlice = new SnapshotSlice<ItemNode, GameNodeOps>(tickState.ItemStore, itemRootList);
        image.ItemSlice.IncrementRootRefCounts();

        // ── 5. Reset buffers for the next tick ──────────────────────────
        tickState.Reset();

        return image;
    }

    public void ReleaseResources(GameWorldImage payload)
    {
        _tickState.MachineStore.DecrementRoots(payload.MachineSlice.Roots);
        _tickState.BeltStore.DecrementRoots(payload.BeltSlice.Roots);
        _tickState.ItemStore.DecrementRoots(payload.ItemSlice.Roots);
        _imagePool.Return(payload);
    }
}
