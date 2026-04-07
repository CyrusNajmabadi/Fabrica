using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.Pipeline;

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
        var image = _imagePool.Rent();
        var tickState = _tickState;

        // ── 1. Wire the pre-allocated job DAG ─────────────────────────────
        tickState.SpawnJob.Reset();
        tickState.BeltJob.Reset();
        tickState.MachineJob.Reset();

        tickState.SpawnJob.ItemThreadLocalBuffers = tickState.ItemStore.ThreadLocalBuffers;
        tickState.SpawnJob.Count = 4;

        tickState.BeltJob.BeltThreadLocalBuffers = tickState.BeltStore.ThreadLocalBuffers;
        tickState.BeltJob.SpawnJob = tickState.SpawnJob;
        tickState.BeltJob.ChainLength = 4;

        tickState.MachineJob.MachineThreadLocalBuffers = tickState.MachineStore.ThreadLocalBuffers;
        tickState.MachineJob.BeltJob = tickState.BeltJob;

        // ── 2. Execute ──────────────────────────────────────────────────
        _scheduler.Submit(tickState.SpawnJob);

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
