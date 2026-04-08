using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.SampleGame.Benchmarks.Scale;

namespace Fabrica.SampleGame.Benchmarks;

/// <summary>
/// Models a realistic game tick: 4 sequential phases with parallel fan-out within each phase.
/// Each job does ~30-40μs of real CPU work (hash-mixing), and the final phase also allocates
/// nodes through the arena system.
///
///   Phase 1 (AI/Decision):     64 ComputeJobs
///   Phase 2 (Physics):         64 ComputeJobs
///   Phase 3 (World Update):    32 ComputeJobs
///   Phase 4 (Snapshot):        16 SnapshotJobs + 1 SnapshotCollectorJob
///
/// Total: ~178 jobs. With 16 cores, each parallel phase has enough work to keep all cores busy.
/// </summary>
[MemoryDiagnoser]
[EventPipeProfiler(EventPipeProfile.CpuSampling)]
public class RealisticTickBenchmark
{
    private const int Phase1Count = 64;
    private const int Phase2Count = 64;
    private const int Phase3Count = 32;
    private const int Phase4Count = 16;
    private const int SnapshotNodeCount = 10;

    /// <summary>
    /// Hash-mixing iterations per job. Calibrate so each job takes ~30-40μs.
    /// </summary>
    private const int ComputeIterations = 50_000;

    private WorkerPool _pool = null!;
    private JobScheduler _scheduler = null!;
    private GlobalNodeStore<BenchNode, BenchNodeOps> _store = null!;
    private MergeCoordinator _coordinator;

    private int[] _workArray = null!;

    private TriggerJob _trigger = null!;
    private ComputeJob[] _phase1 = null!;
    private BarrierJob _barrier1 = null!;
    private ComputeJob[] _phase2 = null!;
    private BarrierJob _barrier2 = null!;
    private ComputeJob[] _phase3 = null!;
    private BarrierJob _barrier3 = null!;
    private SnapshotJob[] _phase4 = null!;
    private SnapshotCollectorJob _collector = null!;

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

        _workArray = new int[64];

        this.AllocateJobs();

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
        _trigger = new TriggerJob();

        _phase1 = new ComputeJob[Phase1Count];
        for (var i = 0; i < Phase1Count; i++)
            _phase1[i] = new ComputeJob();

        _barrier1 = new BarrierJob();

        _phase2 = new ComputeJob[Phase2Count];
        for (var i = 0; i < Phase2Count; i++)
            _phase2[i] = new ComputeJob();

        _barrier2 = new BarrierJob();

        _phase3 = new ComputeJob[Phase3Count];
        for (var i = 0; i < Phase3Count; i++)
            _phase3[i] = new ComputeJob();

        _barrier3 = new BarrierJob();

        _phase4 = new SnapshotJob[Phase4Count];
        for (var i = 0; i < Phase4Count; i++)
            _phase4[i] = new SnapshotJob();

        _collector = new SnapshotCollectorJob
        {
            Sources = _phase4,
        };
    }

    // ── Per-tick wiring ──────────────────────────────────────────────────

    private void WireAndSubmit()
    {
        var buffers = _store.ThreadLocalBuffers;

        _trigger.Reset();

        // Phase 1: AI/Decision — 64 compute jobs, all depend on trigger
        for (var i = 0; i < _phase1.Length; i++)
        {
            var job = _phase1[i];
            job.Reset();
            job.WorkArray = _workArray;
            job.Iterations = ComputeIterations;
            job.Seed = i;
            job.DependsOn(_trigger);
        }

        // Barrier 1: depends on all phase 1 jobs
        _barrier1.Reset();
        for (var i = 0; i < _phase1.Length; i++)
            _barrier1.DependsOn(_phase1[i]);

        // Phase 2: Physics — 64 compute jobs, all depend on barrier 1
        for (var i = 0; i < _phase2.Length; i++)
        {
            var job = _phase2[i];
            job.Reset();
            job.WorkArray = _workArray;
            job.Iterations = ComputeIterations;
            job.Seed = Phase1Count + i;
            job.DependsOn(_barrier1);
        }

        // Barrier 2: depends on all phase 2 jobs
        _barrier2.Reset();
        for (var i = 0; i < _phase2.Length; i++)
            _barrier2.DependsOn(_phase2[i]);

        // Phase 3: World Update — 32 compute jobs, all depend on barrier 2
        for (var i = 0; i < _phase3.Length; i++)
        {
            var job = _phase3[i];
            job.Reset();
            job.WorkArray = _workArray;
            job.Iterations = ComputeIterations;
            job.Seed = Phase1Count + Phase2Count + i;
            job.DependsOn(_barrier2);
        }

        // Barrier 3: depends on all phase 3 jobs
        _barrier3.Reset();
        for (var i = 0; i < _phase3.Length; i++)
            _barrier3.DependsOn(_phase3[i]);

        // Phase 4: Snapshot — 16 snapshot jobs, all depend on barrier 3
        for (var i = 0; i < _phase4.Length; i++)
        {
            var job = _phase4[i];
            job.Reset();
            job.WorkArray = _workArray;
            job.Iterations = ComputeIterations;
            job.Seed = Phase1Count + Phase2Count + Phase3Count + i;
            job.Buffers = buffers;
            job.NodeCount = SnapshotNodeCount;
            job.IsRoot = false;
            job.DependsOn(_barrier3);
        }

        // Collector: fan-in from all snapshot jobs
        _collector.Reset();
        _collector.Buffers = buffers;
        for (var i = 0; i < _phase4.Length; i++)
            _collector.DependsOn(_phase4[i]);

        _scheduler.Submit(_trigger);
    }
}
