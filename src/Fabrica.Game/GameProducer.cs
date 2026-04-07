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
        var image = _imagePool.Rent();
        var tickState = _tickState;

        // ── 1. Reset buffers from the prior tick ────────────────────────
        foreach (var threadLocalBuffer in tickState.MachineThreadLocalBuffers) threadLocalBuffer.Reset();
        foreach (var threadLocalBuffer in tickState.BeltThreadLocalBuffers) threadLocalBuffer.Reset();
        foreach (var threadLocalBuffer in tickState.ItemThreadLocalBuffers) threadLocalBuffer.Reset();
        tickState.MachineRemap.Reset();
        tickState.BeltRemap.Reset();
        tickState.ItemRemap.Reset();

        // ── 2. Build the job DAG ────────────────────────────────────────
        // Dependencies are wired automatically by property setters (DependsOn).
        var spawnJob = new SpawnItemsJob { ItemThreadLocalBuffers = tickState.ItemThreadLocalBuffers, Count = 4 };
        var beltJob = new BuildBeltChainJob
        {
            BeltThreadLocalBuffers = tickState.BeltThreadLocalBuffers,
            SpawnJob = spawnJob,
            ChainLength = 4,
        };
        _ = new PlaceMachinesJob { MachineThreadLocalBuffers = tickState.MachineThreadLocalBuffers, BeltJob = beltJob };

        // ── 3. Execute ──────────────────────────────────────────────────
        _scheduler.Submit(spawnJob);

        // ── 4. Merge pipeline ───────────────────────────────────────────

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

        // Phase 3: collect and remap root handles (only machines are roots).
        tickState.MachineRoots.Reset();
        tickState.MachineStore.CollectAndRemapRoots(
            tickState.MachineThreadLocalBuffers, tickState.MachineRemap, tickState.MachineRoots);

        // ── 5. Build snapshot slices ────────────────────────────────────
        var machineRootList = new UnsafeList<Handle<MachineNode>>();
        foreach (var root in tickState.MachineRoots.WrittenSpan)
            machineRootList.Add(root);

        image.MachineSlice = new SnapshotSlice<MachineNode, GameNodeOps>(tickState.MachineStore, machineRootList);
        image.MachineSlice.IncrementRootRefCounts();

        return image;
    }

    public void ReleaseResources(GameWorldImage payload)
    {
        _tickState.MachineStore.DecrementRoots(payload.MachineSlice.Roots);
        _imagePool.Return(payload);
    }
}
