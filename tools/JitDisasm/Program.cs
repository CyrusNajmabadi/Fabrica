using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.SampleGame.Benchmarks.Scale;

const int Phase1Count = 192;
const int Phase2Count = 192;
const int Phase3Count = 96;
const int Phase4Count = 48;
const int SnapshotNodeCount = 10;
const int ComputeIterations = 25_000;

var pool = new WorkerPool(coordinatorCount: 1);
var scheduler = new JobScheduler(pool);
var store = new GlobalNodeStore<BenchNode, BenchNodeOps>(pool.WorkerCount);
store.SetNodeOps(new BenchNodeOps { Store = store });
var coordinator = new MergeCoordinator([store]);

var trigger = new TriggerJob();

var phase1 = new ComputeJob[Phase1Count];
for (var i = 0; i < Phase1Count; i++)
    phase1[i] = new ComputeJob();

var barrier1 = new BarrierJob();

var phase2 = new ComputeJob[Phase2Count];
for (var i = 0; i < Phase2Count; i++)
    phase2[i] = new ComputeJob();

var barrier2 = new BarrierJob();

var phase3 = new ComputeJob[Phase3Count];
for (var i = 0; i < Phase3Count; i++)
    phase3[i] = new ComputeJob();

var barrier3 = new BarrierJob();

var phase4 = new SnapshotJob[Phase4Count];
for (var i = 0; i < Phase4Count; i++)
    phase4[i] = new SnapshotJob();

var collector = new SnapshotCollectorJob { Sources = phase4 };

var buffers = store.ThreadLocalBuffers;
var hasPreviousSlice = false;
SnapshotSlice<BenchNode, BenchNodeOps> previousSlice = default;

for (var tick = 0; tick < 3; tick++)
{
    trigger.Reset();

    for (var i = 0; i < phase1.Length; i++)
    {
        var job = phase1[i];
        job.Reset();
        job.Iterations = ComputeIterations;
        job.Seed = i;
        job.DependsOn(trigger);
    }

    barrier1.Reset();
    for (var i = 0; i < phase1.Length; i++)
        barrier1.DependsOn(phase1[i]);

    for (var i = 0; i < phase2.Length; i++)
    {
        var job = phase2[i];
        job.Reset();
        job.Iterations = ComputeIterations;
        job.Seed = Phase1Count + i;
        job.DependsOn(barrier1);
    }

    barrier2.Reset();
    for (var i = 0; i < phase2.Length; i++)
        barrier2.DependsOn(phase2[i]);

    for (var i = 0; i < phase3.Length; i++)
    {
        var job = phase3[i];
        job.Reset();
        job.Iterations = ComputeIterations;
        job.Seed = Phase1Count + Phase2Count + i;
        job.DependsOn(barrier2);
    }

    barrier3.Reset();
    for (var i = 0; i < phase3.Length; i++)
        barrier3.DependsOn(phase3[i]);

    for (var i = 0; i < phase4.Length; i++)
    {
        var job = phase4[i];
        job.Reset();
        job.Iterations = ComputeIterations;
        job.Seed = Phase1Count + Phase2Count + Phase3Count + i;
        job.Buffers = buffers;
        job.NodeCount = SnapshotNodeCount;
        job.IsRoot = false;
        job.DependsOn(barrier3);
    }

    collector.Reset();
    collector.Buffers = buffers;
    for (var i = 0; i < phase4.Length; i++)
        collector.DependsOn(phase4[i]);

    scheduler.Submit(trigger);

    SnapshotSlice<BenchNode, BenchNodeOps> slice;
    using (var merge = coordinator.MergeAll())
        slice = store.BuildSnapshotSlice();

    if (hasPreviousSlice)
        store.ReleaseSnapshotSlice(previousSlice);

    previousSlice = slice;
    hasPreviousSlice = true;
}

if (hasPreviousSlice)
    store.ReleaseSnapshotSlice(previousSlice);

pool.Dispose();
Console.WriteLine("JIT disasm harness complete.");
