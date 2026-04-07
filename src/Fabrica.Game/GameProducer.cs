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
        //   3. Merge: drain all → rewrite + refcount → build snapshot slices.
        //   4. Reset per-worker buffers and remap tables for the next tick.

        var image = _imagePool.Rent();
        var tickState = _tickState;

        // ── 1. Build the job DAG ─────────────────────────────────────────
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

        // Phase 1: drain all TLBs (barrier — all drains must complete before rewrite).
        tickState.Coordinator.DrainAll();

        // Phase 2: rewrite local handles to global and increment child refcounts.
        var refcountVisitor = new GameRefcountVisitor
        {
            MachineStore = tickState.MachineStore,
            BeltStore = tickState.BeltStore,
            ItemStore = tickState.ItemStore,
        };
        tickState.ItemStore.RewriteAndIncrementRefCounts(ref refcountVisitor);
        tickState.BeltStore.RewriteAndIncrementRefCounts(ref refcountVisitor);
        tickState.MachineStore.RewriteAndIncrementRefCounts(ref refcountVisitor);

        // Phase 3+4: collect roots, build slices, increment root refcounts.
        image.MachineSlice = tickState.MachineStore.BuildSnapshotSlice();
        image.BeltSlice = tickState.BeltStore.BuildSnapshotSlice();
        image.ItemSlice = tickState.ItemStore.BuildSnapshotSlice();

        // ── 4. Reset ────────────────────────────────────────────────────
        tickState.Coordinator.ResetAll();

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
