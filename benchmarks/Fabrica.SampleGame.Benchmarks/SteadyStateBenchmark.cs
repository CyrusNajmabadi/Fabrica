using BenchmarkDotNet.Attributes;
using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;

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

        _scheduler.Submit(tickState.SpawnJob);

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
