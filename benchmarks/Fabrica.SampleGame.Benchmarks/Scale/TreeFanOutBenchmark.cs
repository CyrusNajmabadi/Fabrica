using System.Diagnostics;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using Fabrica.Core.Jobs;
using Fabrica.SampleGame.Benchmarks.Scale;

namespace Fabrica.SampleGame.Benchmarks;

/// <summary>
/// Measures scheduler throughput on a quad-tree DAG (fan-out = 4, depth = 6). Unlike
/// <see cref="RealisticTickBenchmark"/> which uses wide global barriers, this benchmark has
/// only local parent-child dependencies. The frontier of runnable jobs grows exponentially
/// with depth, so cores should stay saturated without synchronization stalls.
///
///   Depth 0: 1 root
///   Depth 1: 4 nodes
///   Depth 2: 16 nodes
///   ...
///   Depth 6: 4096 leaf nodes
///
/// Total: (4^7 - 1) / 3 = 5461 compute jobs.
/// Critical path: 7 sequential jobs (root → leaf).
/// Optimal = (5461 × per-job work) / 12 cores.
/// </summary>
[Config(typeof(DiagnoserConfig))]
[EventPipeProfiler(EventPipeProfile.CpuSampling)]
public class TreeFanOutBenchmark
{
    private sealed class DiagnoserConfig : ManualConfig
    {
        public DiagnoserConfig()
        {
            this.AddDiagnoser(MemoryDiagnoser.Default);
            this.AddDiagnoser(ThreadingDiagnoser.Default);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                this.AddDiagnoser(new DisassemblyDiagnoser(new DisassemblyDiagnoserConfig(maxDepth: 3, printSource: true)));

            this.AddColumn(StatisticColumn.Median);
            this.AddColumn(StatisticColumn.P95);
            this.AddColumn(StatisticColumn.OperationsPerSecond);
        }
    }

    private const int FanOut = 4;
    private const int Depth = 6;
    private const int Cores = 12;
    private const int ComputeIterations = 12_500;

    /// <summary>Total nodes in a complete k-ary tree: (k^(d+1) - 1) / (k - 1).</summary>
    private const int TotalJobs = ((1 << ((Depth + 1) * 2)) - 1) / (FanOut - 1); // 5461

    private WorkerPool _pool = null!;
    private JobScheduler _scheduler = null!;
    private ComputeJob[] _jobs = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        Console.WriteLine($"UNSAFE_OPT (BuildConfig.UnsafeOptimizations): {Fabrica.Core.BuildConfig.UnsafeOptimizations}");
        Console.WriteLine($"Tree: fan-out={FanOut}, depth={Depth}, total jobs={TotalJobs}");

        _pool = new WorkerPool(coordinatorCount: 1);
        _scheduler = new JobScheduler(_pool);

        _jobs = new ComputeJob[TotalJobs];
        for (var i = 0; i < TotalJobs; i++)
            _jobs[i] = new ComputeJob(_scheduler);

        for (var i = 0; i < 10; i++)
            this.RunOneTick();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        this.RunAnalysis();
        _pool.Dispose();
    }

    [Benchmark]
    public void OneTick() => this.RunOneTick();

    private void RunOneTick() => this.WireAndSubmit();

    // ── Per-tick wiring ──────────────────────────────────────────────────

    private void WireAndSubmit()
    {
        for (var i = 0; i < TotalJobs; i++)
        {
            var job = _jobs[i];
            job.Reset();
            job.Iterations = ComputeIterations;
            job.Seed = i;

            if (i > 0)
            {
                var parentIndex = (i - 1) / FanOut;
                job.DependsOn(_jobs[parentIndex]);
            }
        }

        _scheduler.Submit(_jobs[0]);
    }

    // ── Analysis ─────────────────────────────────────────────────────────

    private void RunAnalysis()
    {
        const int AnalysisTicks = 2000;

        this.SetInstrument(true);
        var snapshots = new TreeSnapshot[AnalysisTicks];

        for (var t = 0; t < AnalysisTicks; t++)
        {
            var submitTs = Stopwatch.GetTimestamp();
            this.WireAndSubmit();
            var completeTs = Stopwatch.GetTimestamp();

            snapshots[t] = new TreeSnapshot
            {
                SubmitTs = submitTs,
                CompleteTs = completeTs,
                JobStarts = new long[TotalJobs],
                JobEnds = new long[TotalJobs],
            };

            for (var i = 0; i < TotalJobs; i++)
            {
                snapshots[t].JobStarts[i] = _jobs[i].StartTimestamp;
                snapshots[t].JobEnds[i] = _jobs[i].EndTimestamp;
            }
        }

        this.SetInstrument(false);
        PrintAnalysis(snapshots);
    }

    private void SetInstrument(bool enabled)
    {
        foreach (var job in _jobs)
            job.Instrument = enabled;
    }

    private static void PrintAnalysis(TreeSnapshot[] snapshots)
    {
        var freq = (double)Stopwatch.Frequency;
        static double ToUs(long from, long to, double f) => (to - from) / f * 1_000_000;

        var totals = new double[snapshots.Length];
        var criticals = new double[snapshots.Length];
        var works = new double[snapshots.Length];

        for (var t = 0; t < snapshots.Length; t++)
        {
            ref var s = ref snapshots[t];
            totals[t] = ToUs(s.SubmitTs, s.CompleteTs, freq);

            var totalWork = 0.0;
            for (var i = 0; i < TotalJobs; i++)
                totalWork += ToUs(s.JobStarts[i], s.JobEnds[i], freq);
            works[t] = totalWork;

            // Critical path: longest root-to-leaf chain (depth 0 → depth Depth).
            // Walk each leaf back to root, summing job durations.
            var firstLeaf = (TotalJobs - (int)Math.Pow(FanOut, Depth));
            var maxCritical = 0.0;
            for (var leaf = firstLeaf; leaf < TotalJobs; leaf++)
            {
                var pathTime = 0.0;
                var node = leaf;
                while (node >= 0)
                {
                    pathTime += ToUs(s.JobStarts[node], s.JobEnds[node], freq);
                    node = node > 0 ? (node - 1) / FanOut : -1;
                }

                if (pathTime > maxCritical)
                    maxCritical = pathTime;
            }

            criticals[t] = maxCritical;
        }

        Array.Sort(totals);
        Array.Sort(criticals);
        Array.Sort(works);

        static double Percentile(double[] sorted, double pct) => sorted[(int)(sorted.Length * pct)];

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  TREE FAN-OUT BENCHMARK ANALYSIS");
        Console.WriteLine($"  {snapshots.Length} ticks, fan-out={FanOut}, depth={Depth}, {TotalJobs} jobs");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        Console.WriteLine();
        Console.WriteLine("  ┌──────────────────────────┬────────┬────────┬────────┬────────┬────────┐");
        Console.WriteLine("  │ Metric                   │   P1   │  P10   │  P50   │  P90   │  P99   │");
        Console.WriteLine("  ├──────────────────────────┼────────┼────────┼────────┼────────┼────────┤");

        PrintRow("Total tick (μs)", totals);
        PrintRow("Critical path (μs)", criticals);
        PrintRow("Total work (μs)", works);

        Console.WriteLine("  └──────────────────────────┴────────┴────────┴────────┴────────┴────────┘");

        var p50Total = Percentile(totals, 0.50);
        var p50Work = Percentile(works, 0.50);
        var p50Critical = Percentile(criticals, 0.50);
        var optimal = p50Work / Cores;

        Console.WriteLine();
        Console.WriteLine($"  P50 total:     {p50Total:F1} μs");
        Console.WriteLine($"  P50 work:      {p50Work:F0} μs");
        Console.WriteLine($"  P50 critical:  {p50Critical:F1} μs (lower bound — {Depth + 1} sequential jobs)");
        Console.WriteLine($"  Optimal (work / {Cores} cores): {optimal:F1} μs");
        Console.WriteLine($"  Gap to optimal: {p50Total - optimal:F1} μs  ({(p50Total - optimal) / optimal * 100:F1}% above optimal)");
        Console.WriteLine($"  Parallelism efficiency: {p50Work / (p50Total * Cores) * 100:F1}%");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
    }

    private static void PrintRow(string label, double[] sorted)
    {
        var pcts = new[] { 0.01, 0.10, 0.50, 0.90, 0.99 };
        Console.Write($"  │ {label,-24} │");
        foreach (var p in pcts)
            Console.Write($" {sorted[(int)(sorted.Length * p)],6:F1} │");
        Console.WriteLine();
    }

    private struct TreeSnapshot
    {
        public long SubmitTs, CompleteTs;
        public long[] JobStarts, JobEnds;
    }
}
