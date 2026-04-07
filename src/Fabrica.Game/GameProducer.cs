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
        var spawnJob = new SpawnItemsJob { ItemThreadLocalBuffers = tickState.ItemStore.ThreadLocalBuffers, Count = 4 };
        var beltJob = new BuildBeltChainJob
        {
            BeltThreadLocalBuffers = tickState.BeltStore.ThreadLocalBuffers,
            SpawnJob = spawnJob,
            ChainLength = 4,
        };
        _ = new PlaceMachinesJob { MachineThreadLocalBuffers = tickState.MachineStore.ThreadLocalBuffers, BeltJob = beltJob };

        // ── 2. Execute ──────────────────────────────────────────────────
        _scheduler.Submit(spawnJob);

        // ── 3. Merge pipeline ───────────────────────────────────────────

        // Phase 1: drain all TLBs into global arenas and build remap tables.
        var (itemStart, itemCount) = tickState.ItemStore.DrainBuffers();
        var (beltStart, beltCount) = tickState.BeltStore.DrainBuffers();
        var (machineStart, machineCount) = tickState.MachineStore.DrainBuffers();

        // Phase 2: rewrite local handles to global and increment child refcounts.
        var refcountVisitor = new GameRefcountVisitor
        {
            MachineStore = tickState.MachineStore,
            BeltStore = tickState.BeltStore,
            ItemStore = tickState.ItemStore,
        };

        tickState.ItemStore.RewriteAndIncrementRefCounts(itemStart, itemCount, ref refcountVisitor);
        tickState.BeltStore.RewriteAndIncrementRefCounts(beltStart, beltCount, ref refcountVisitor);
        tickState.MachineStore.RewriteAndIncrementRefCounts(machineStart, machineCount, ref refcountVisitor);

        // Phase 3: collect roots directly into the lists that the snapshot slices will own.
        var machineRoots = new UnsafeList<Handle<MachineNode>>();
        tickState.MachineStore.CollectAndRemapRoots(machineRoots);

        var beltRoots = new UnsafeList<Handle<BeltSegmentNode>>();
        tickState.BeltStore.CollectAndRemapRoots(beltRoots);

        var itemRoots = new UnsafeList<Handle<ItemNode>>();
        tickState.ItemStore.CollectAndRemapRoots(itemRoots);

        // ── 4. Build snapshot slices ────────────────────────────────────
        image.MachineSlice = new SnapshotSlice<MachineNode, GameNodeOps>(tickState.MachineStore, machineRoots);
        image.MachineSlice.IncrementRootRefCounts();

        image.BeltSlice = new SnapshotSlice<BeltSegmentNode, GameNodeOps>(tickState.BeltStore, beltRoots);
        image.BeltSlice.IncrementRootRefCounts();

        image.ItemSlice = new SnapshotSlice<ItemNode, GameNodeOps>(tickState.ItemStore, itemRoots);
        image.ItemSlice.IncrementRootRefCounts();

        // ── 5. Reset buffers for the next tick ──────────────────────────
        tickState.Reset();

        return image;
    }

    public void ReleaseResources(GameWorldImage payload)
    {
        payload.MachineSlice.Release();
        payload.BeltSlice.Release();
        payload.ItemSlice.Release();
        _imagePool.Return(payload);
    }
}
