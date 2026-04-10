using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.SampleGame.Benchmarks.Scale;

namespace Fabrica.SampleGame.Benchmarks;

/// <summary>
/// Rebuilds a complete 41,545-node tree each tick using 4,682 jobs (fan-out 8, depth 5).
/// Validates zero steady-state allocation and measures full pipeline throughput.
/// </summary>
[MemoryDiagnoser]
[EventPipeProfiler(EventPipeProfile.CpuSampling)]
public class FullRebuildBenchmark
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

    private SnapshotSlice<BenchNode, BenchNodeOps> _previousSlice;
    private bool _hasPreviousSlice;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _pool = new WorkerPool(coordinatorCount: 1);
        _scheduler = new JobScheduler(_pool);
        _store = new GlobalNodeStore<BenchNode, BenchNodeOps>(_pool.WorkerCount);
        var ops = new BenchNodeOps { Store = _store };
        _store.SetNodeOps(ops);
        _coordinator = new MergeCoordinator([_store]);

        this.AllocateJobs();

        // Warm up: first tick populates arenas/free-lists, subsequent ticks reach steady state.
        for (var i = 0; i < 10; i++)
            this.RunOneTick();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        if (_hasPreviousSlice)
            _store.ReleaseSnapshotSlice(_previousSlice);
        _pool.Dispose();
    }

    [Benchmark]
    public void OneTick() => this.RunOneTick();

    private void RunOneTick()
    {
        this.WireAndSubmit();

        SnapshotSlice<BenchNode, BenchNodeOps> slice;
        using (var merge = _coordinator.MergeAll())
            slice = _store.BuildSnapshotSlice();

        if (_hasPreviousSlice)
            _store.ReleaseSnapshotSlice(_previousSlice);

        _previousSlice = slice;
        _hasPreviousSlice = true;
    }

    // ── Job allocation (once) ────────────────────────────────────────────

    private void AllocateJobs()
    {
        var leafCount = (int)Math.Pow(FanOut, Depth - 1); // 8^4 = 4096
        _trigger = new TriggerJob(_scheduler);
        _leaves = new LeafJob[leafCount];
        for (var i = 0; i < leafCount; i++)
            _leaves[i] = new LeafJob(_scheduler);

        // Collector levels: L3 (512), L2 (64), L1 (8), L0 (1)
        _collectorLevels = new CollectorJob[Depth - 1][];
        for (var level = 0; level < Depth - 1; level++)
        {
            var count = (int)Math.Pow(FanOut, Depth - 2 - level);
            _collectorLevels[level] = new CollectorJob[count];
            for (var i = 0; i < count; i++)
            {
                _collectorLevels[level][i] = new CollectorJob(_scheduler)
                {
                    Children = new TreeJob[FanOut],
                };
            }
        }
    }

    // ── Per-tick wiring ──────────────────────────────────────────────────

    private void WireAndSubmit()
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

        // L3 collectors: each depends on 8 leaves.
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

        // L2, L1, L0: each depends on 8 children from the previous collector level.
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
    }
}
