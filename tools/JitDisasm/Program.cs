using Fabrica.Core.Jobs;
using Fabrica.SampleGame.Benchmarks.Scale;

const int JobsPerPhase = 192;
const int PhaseCount = 4;
const int ComputeIterations = 25_000;

var pool = new WorkerPool(coordinatorCount: 1);
var scheduler = new JobScheduler(pool);

var trigger = new TriggerJob();
var phases = new ComputeJob[PhaseCount][];
var barriers = new BarrierJob[PhaseCount];

for (var p = 0; p < PhaseCount; p++)
{
    phases[p] = new ComputeJob[JobsPerPhase];
    for (var i = 0; i < JobsPerPhase; i++)
        phases[p][i] = new ComputeJob();
    barriers[p] = new BarrierJob();
}

// Run enough ticks to trigger tier-1 PGO recompilation (default threshold ~30 calls).
for (var tick = 0; tick < 100; tick++)
{
    trigger.Reset();

    for (var p = 0; p < PhaseCount; p++)
    {
        var prev = p == 0 ? (Job)trigger : barriers[p - 1];
        for (var i = 0; i < JobsPerPhase; i++)
        {
            var job = phases[p][i];
            job.Reset();
            job.Iterations = ComputeIterations;
            job.Seed = p * JobsPerPhase + i;
            job.DependsOn(prev);
        }

        barriers[p].Reset();
        for (var i = 0; i < JobsPerPhase; i++)
            barriers[p].DependsOn(phases[p][i]);
    }

    scheduler.Submit(trigger);
}

pool.Dispose();
Console.WriteLine("JIT disasm harness complete.");
