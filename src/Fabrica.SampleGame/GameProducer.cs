using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.Pipeline;
using Fabrica.SampleGame.Jobs;

namespace Fabrica.SampleGame;

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

        using (var merge = tickState.Coordinator.MergeAll())
        {
            image.MachineSlice = tickState.MachineStore.BuildSnapshotSlice();
            image.BeltSlice = tickState.BeltStore.BuildSnapshotSlice();
            image.ItemSlice = tickState.ItemStore.BuildSnapshotSlice();
        }

        return image;
    }

    public void ReleaseResources(GameWorldImage payload)
    {
        _tickState.MachineStore.ReleaseSnapshotSlice(payload.MachineSlice);
        _tickState.BeltStore.ReleaseSnapshotSlice(payload.BeltSlice);
        _tickState.ItemStore.ReleaseSnapshotSlice(payload.ItemSlice);
        _imagePool.Return(payload);
    }
}
