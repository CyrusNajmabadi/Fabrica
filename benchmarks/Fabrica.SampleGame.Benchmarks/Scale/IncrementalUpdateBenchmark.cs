using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.SampleGame.Benchmarks.Scale;

namespace Fabrica.SampleGame.Benchmarks;

/// <summary>
/// Builds a 41,545-node tree once, then each tick replaces one leaf chain + spine (14 new nodes).
/// Measures the persistent-data-structure structural sharing path: minimal new work, maximal reuse.
/// </summary>
[MemoryDiagnoser]
[EventPipeProfiler(EventPipeProfile.CpuSampling)]
public class IncrementalUpdateBenchmark
{
    private const int FanOut = 8;
    private const int Depth = 5;
    private const int LeafChainLength = 10;

    private WorkerPool _pool = null!;
    private JobScheduler _scheduler = null!;
    private GlobalNodeStore<BenchNode, BenchNodeOps> _store = null!;
    private MergeCoordinator _coordinator;

    private TriggerJob _trigger = null!;
    private LeafJob[] _leaves = null!;
    private CollectorJob[][] _collectorLevels = null!;

    private SpineUpdateJob _spineJob = null!;

    private SnapshotSlice<BenchNode, BenchNodeOps> _currentSlice;
    private Handle<BenchNode> _currentRoot;
    private int _tickCounter;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _pool = new WorkerPool(coordinatorCount: 1);
        _scheduler = new JobScheduler(_pool);
        _store = new GlobalNodeStore<BenchNode, BenchNodeOps>(_pool.WorkerCount);
        var ops = new BenchNodeOps { Store = _store };
        _store.SetNodeOps(ops);
        _coordinator = new MergeCoordinator([_store]);

        this.AllocateFullRebuildJobs();
        this.BuildInitialTree();

        _spineJob = new SpineUpdateJob();

        // Warm up the incremental path to reach steady state.
        for (var i = 0; i < 10; i++)
            this.RunOneTick();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _store.ReleaseSnapshotSlice(_currentSlice);
        _pool.Dispose();
    }

    [Benchmark]
    public void OneTick() => this.RunOneTick();

    private void RunOneTick()
    {
        var tick = _tickCounter++;
        var totalLeaves = (int)Math.Pow(FanOut, Depth - 1); // 4096

        // Deterministic path: rotate through leaves so every leaf is eventually updated.
        var leafIndex = tick % totalLeaves;
        var idx3 = leafIndex % FanOut;
        var idx2 = leafIndex / FanOut % FanOut;
        var idx1 = leafIndex / (FanOut * FanOut) % FanOut;
        var idx0 = leafIndex / (FanOut * FanOut * FanOut) % FanOut;

        _spineJob.Reset();
        _spineJob.Buffers = _store.ThreadLocalBuffers;
        _spineJob.Arena = _store.Arena;
        _spineJob.CurrentRoot = _currentRoot;
        _spineJob.PathIndex0 = idx0;
        _spineJob.PathIndex1 = idx1;
        _spineJob.PathIndex2 = idx2;
        _spineJob.PathIndex3 = idx3;
        _spineJob.NewLeafValue = tick;

        _scheduler.Submit(_spineJob);

        SnapshotSlice<BenchNode, BenchNodeOps> newSlice;
        using (var merge = _coordinator.MergeAll())
            newSlice = _store.BuildSnapshotSlice();

        _store.ReleaseSnapshotSlice(_currentSlice);

        _currentSlice = newSlice;
        _currentRoot = newSlice.Roots[0];
    }

    // ── Initial full tree build (runs once in GlobalSetup) ───────────────

    private void AllocateFullRebuildJobs()
    {
        var leafCount = (int)Math.Pow(FanOut, Depth - 1);
        _trigger = new TriggerJob();
        _leaves = new LeafJob[leafCount];
        for (var i = 0; i < leafCount; i++)
            _leaves[i] = new LeafJob();

        _collectorLevels = new CollectorJob[Depth - 1][];
        for (var level = 0; level < Depth - 1; level++)
        {
            var count = (int)Math.Pow(FanOut, Depth - 2 - level);
            _collectorLevels[level] = new CollectorJob[count];
            for (var i = 0; i < count; i++)
            {
                _collectorLevels[level][i] = new CollectorJob
                {
                    Children = new TreeJob[FanOut],
                };
            }
        }
    }

    private void BuildInitialTree()
    {
        var buffers = _store.ThreadLocalBuffers;

        _trigger.Reset();
        for (var i = 0; i < _leaves.Length; i++)
        {
            var leaf = _leaves[i];
            leaf.Reset();
            leaf.Buffers = buffers;
            leaf.ChainLength = LeafChainLength;
            leaf.DependsOn(_trigger);
        }

        var l3 = _collectorLevels[0];
        for (var i = 0; i < l3.Length; i++)
        {
            var c = l3[i];
            c.Reset();
            c.Buffers = buffers;
            c.IsRoot = false;
            for (var j = 0; j < FanOut; j++)
            {
                c.Children[j] = _leaves[(i * FanOut) + j];
                c.DependsOn(c.Children[j]);
            }
        }

        for (var level = 1; level < _collectorLevels.Length; level++)
        {
            var current = _collectorLevels[level];
            var previous = _collectorLevels[level - 1];
            for (var i = 0; i < current.Length; i++)
            {
                var c = current[i];
                c.Reset();
                c.Buffers = buffers;
                c.IsRoot = level == _collectorLevels.Length - 1 && i == 0;
                for (var j = 0; j < FanOut; j++)
                {
                    c.Children[j] = previous[(i * FanOut) + j];
                    c.DependsOn(c.Children[j]);
                }
            }
        }

        _scheduler.Submit(_trigger);

        using (var merge = _coordinator.MergeAll())
            _currentSlice = _store.BuildSnapshotSlice();

        _currentRoot = _currentSlice.Roots[0];
    }
}
