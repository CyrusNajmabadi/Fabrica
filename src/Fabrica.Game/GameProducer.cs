using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.Game.Jobs;
using Fabrica.Game.Nodes;
using Fabrica.Pipeline;

namespace Fabrica.Game;

/// <summary>
/// Produces one <see cref="GameWorldImage"/> per tick. Each tick:
///   1. Resets per-worker TLBs and remap tables.
///   2. Builds the 3-job DAG (SpawnItems -> BuildBelts -> PlaceMachines).
///   3. Submits via <see cref="JobScheduler"/> (blocks until the DAG completes).
///   4. Runs the merge pipeline: drain -> rewrite + refcount -> collect roots.
///   5. Builds <see cref="SnapshotSlice{TNode,TNodeOps}"/> instances and increments root refcounts.
/// </summary>
public readonly struct GameProducer : IProducer<GameWorldImage>
{
    private readonly ObjectPool<GameWorldImage, GameWorldImage.Allocator> _imagePool;
    private readonly JobScheduler _scheduler;
    private readonly GameTickState _tickState;

    internal GameProducer(
        ObjectPool<GameWorldImage, GameWorldImage.Allocator> imagePool,
        JobScheduler scheduler,
        GameTickState tickState)
    {
        _imagePool = imagePool;
        _scheduler = scheduler;
        _tickState = tickState;
    }

    public GameWorldImage CreateInitialPayload(CancellationToken cancellationToken)
        => _imagePool.Rent();

    public GameWorldImage Produce(GameWorldImage current, CancellationToken cancellationToken)
    {
        var image = _imagePool.Rent();
        var ts = _tickState;

        // ── 1. Reset buffers from the prior tick ────────────────────────
        foreach (var tlb in ts.MachineTlbs) tlb.Reset();
        foreach (var tlb in ts.BeltTlbs) tlb.Reset();
        foreach (var tlb in ts.ItemTlbs) tlb.Reset();
        ts.MachineRemap.Reset();
        ts.BeltRemap.Reset();
        ts.ItemRemap.Reset();

        // ── 2. Build the job DAG ────────────────────────────────────────
        // Dependencies are wired automatically by property setters (DependsOn).
        var spawnJob = new SpawnItemsJob { ItemTlbs = ts.ItemTlbs, Count = 4 };
        var beltJob = new BuildBeltChainJob { BeltTlbs = ts.BeltTlbs, SpawnJob = spawnJob, ChainLength = 4 };
        _ = new PlaceMachinesJob { MachineTlbs = ts.MachineTlbs, BeltJob = beltJob };

        // ── 3. Execute ──────────────────────────────────────────────────
        _scheduler.Submit(spawnJob);

        // ── 4. Merge pipeline ───────────────────────────────────────────

        // Phase 1: drain all TLBs into global arenas and build remap tables.
        var (itemStart, itemCount) = ts.ItemStore.DrainBuffers(ts.ItemTlbs, ts.ItemRemap);
        var (beltStart, beltCount) = ts.BeltStore.DrainBuffers(ts.BeltTlbs, ts.BeltRemap);
        var (machineStart, machineCount) = ts.MachineStore.DrainBuffers(ts.MachineTlbs, ts.MachineRemap);

        // Phase 2: rewrite local handles to global and increment child refcounts.
        var remapVisitor = new GameRemapVisitor
        {
            MachineRemap = ts.MachineRemap,
            BeltRemap = ts.BeltRemap,
            ItemRemap = ts.ItemRemap,
        };
        var refcountVisitor = new GameRefcountVisitor
        {
            MachineStore = ts.MachineStore,
            BeltStore = ts.BeltStore,
            ItemStore = ts.ItemStore,
        };

        ts.ItemStore.RewriteAndIncrementRefCounts(itemStart, itemCount, ref remapVisitor, ref refcountVisitor);
        ts.BeltStore.RewriteAndIncrementRefCounts(beltStart, beltCount, ref remapVisitor, ref refcountVisitor);
        ts.MachineStore.RewriteAndIncrementRefCounts(machineStart, machineCount, ref remapVisitor, ref refcountVisitor);

        // Phase 3: collect and remap root handles (only machines are roots).
        ts.MachineRoots.Reset();
        ts.MachineStore.CollectAndRemapRoots(ts.MachineTlbs, ts.MachineRemap, ts.MachineRoots);

        // ── 5. Build snapshot slices ────────────────────────────────────
        var machineRootList = new UnsafeList<Handle<MachineNode>>();
        foreach (var root in ts.MachineRoots.WrittenSpan)
            machineRootList.Add(root);

        image.MachineSlice = new SnapshotSlice<MachineNode, GameNodeOps>(ts.MachineStore, machineRootList);
        image.MachineSlice.IncrementRootRefCounts();

        return image;
    }

    public void ReleaseResources(GameWorldImage payload)
    {
        _tickState.MachineStore.DecrementRoots(payload.MachineSlice.Roots);
        _imagePool.Return(payload);
    }
}
