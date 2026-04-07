using BenchmarkDotNet.Attributes;
using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.SampleGame.Jobs;

namespace Fabrica.SampleGame.Benchmarks;

/// <summary>
/// Validates that the full production tick (job DAG execution, merge pipeline, snapshot
/// build/release) reaches a zero-allocation steady state after warm-up.
/// </summary>
[MemoryDiagnoser]
public class SteadyStateBenchmark
{
    private WorkerPool _pool = null!;
    private JobScheduler _scheduler = null!;
    private GameTickState _tickState = null!;
    private readonly ObjectPool<GameWorldImage, GameWorldImage.Allocator> _imagePool = new(initialCapacity: 8);

    private GameWorldImage _previousImage = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _pool = new WorkerPool(workerCount: 4);
        _scheduler = new JobScheduler(_pool);
        _tickState = new GameTickState(_pool.WorkerCount);

        _previousImage = this.ProduceImage();

        for (var i = 0; i < 9; i++)
            this.RunOneTick();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        this.ReleaseImage(_previousImage);
        _pool.Dispose();
    }

    [Benchmark]
    public void OneTick() => this.RunOneTick();

    private void RunOneTick()
    {
        var image = this.ProduceImage();
        this.ReleaseImage(_previousImage);
        _previousImage = image;
    }

    private GameWorldImage ProduceImage()
    {
        var image = _imagePool.Rent();
        var tickState = _tickState;

        var spawnJob = new SpawnItemsJob { ItemThreadLocalBuffers = tickState.ItemStore.ThreadLocalBuffers, Count = 4 };
        var beltJob = new BuildBeltChainJob
        {
            BeltThreadLocalBuffers = tickState.BeltStore.ThreadLocalBuffers,
            SpawnJob = spawnJob,
            ChainLength = 4,
        };
        _ = new PlaceMachinesJob { MachineThreadLocalBuffers = tickState.MachineStore.ThreadLocalBuffers, BeltJob = beltJob };

        _scheduler.Submit(spawnJob);

        using (var merge = tickState.Coordinator.MergeAll())
        {
            image.MachineSlice = tickState.MachineStore.BuildSnapshotSlice();
            image.BeltSlice = tickState.BeltStore.BuildSnapshotSlice();
            image.ItemSlice = tickState.ItemStore.BuildSnapshotSlice();
        }

        return image;
    }

    private void ReleaseImage(GameWorldImage image)
    {
        _tickState.MachineStore.ReleaseSnapshotSlice(image.MachineSlice);
        _tickState.BeltStore.ReleaseSnapshotSlice(image.BeltSlice);
        _tickState.ItemStore.ReleaseSnapshotSlice(image.ItemSlice);
        _imagePool.Return(image);
    }
}
