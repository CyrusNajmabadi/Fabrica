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
/// Measures pure scheduler overhead: 32 identical sequential phases, each fanning out
/// 192 ComputeJobs across 12 P-cores (16 jobs/core). Every job does ~15-20μs of hash-mixing
/// in L1-resident arrays. No arena allocation or snapshot merging — just compute + scheduling.
///
///   Phase 1 → Phase 2 → … → Phase 32   (each: 192 ComputeJobs, barrier between)
///
/// Total: 6144 compute jobs + 1 trigger + 32 barriers = 6177 jobs per tick.
/// Optimal = (6144 × per-job work) / 12 cores.
/// </summary>
[Config(typeof(DiagnoserConfig))]
[EventPipeProfiler(EventPipeProfile.CpuSampling)]
public class RealisticTickBenchmark
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

    private const int JobsPerPhase = 192;
    private const int PhaseCount = 32;
    private const int Cores = 12;

    /// <summary>
    /// Hash-mixing iterations per job. Calibrate so each job takes ~15-20μs.
    /// </summary>
    private const int ComputeIterations = 12_500;

    private WorkerPool _pool = null!;
    private JobScheduler _scheduler = null!;

    private TriggerJob _trigger = null!;
    private ComputeJob[][] _phases = null!;
    private BarrierJob[] _barriers = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        Console.WriteLine($"UNSAFE_OPT (BuildConfig.UnsafeOptimizations): {Fabrica.Core.BuildConfig.UnsafeOptimizations}");

        _pool = new WorkerPool(coordinatorCount: 1);
        _scheduler = new JobScheduler(_pool);

        this.AllocateJobs();

        for (var i = 0; i < 10; i++)
            this.RunOneTick();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        this.RunPhaseAnalysis();
        this.RunSchedulerAnalysis();
        _pool.Dispose();
    }

    [Benchmark]
    public void OneTick() => this.RunOneTick();

    private void RunOneTick() => this.WireAndSubmit();

    // ── Phase analysis ───────────────────────────────────────────────────

    private void RunPhaseAnalysis()
    {
        const int AnalysisTicks = 2000;

        this.SetInstrument(true);
        var snapshots = new RawSnapshot[AnalysisTicks];

        for (var t = 0; t < AnalysisTicks; t++)
        {
            var submitTs = Stopwatch.GetTimestamp();
            this.WireAndSubmit();
            var dagCompleteTs = Stopwatch.GetTimestamp();

            snapshots[t] = this.CaptureRawSnapshot(submitTs, dagCompleteTs);
        }

        this.SetInstrument(false);
        PrintAnalysis(snapshots);
    }

    private void SetInstrument(bool enabled)
    {
        _trigger.Instrument = enabled;
        foreach (var barrier in _barriers) barrier.Instrument = enabled;
        foreach (var phase in _phases)
            foreach (var j in phase) j.Instrument = enabled;
    }

    private RawSnapshot CaptureRawSnapshot(long submitTs, long dagCompleteTs)
    {
        var snap = new RawSnapshot
        {
            SubmitTs = submitTs,
            DagCompleteTs = dagCompleteTs,
            TriggerTs = _trigger.ExecutedTimestamp,
            BarrierTs = new long[PhaseCount],
            PhaseJobStarts = new long[PhaseCount][],
            PhaseJobEnds = new long[PhaseCount][],
        };

        for (var p = 0; p < PhaseCount; p++)
        {
            snap.BarrierTs[p] = _barriers[p].ExecutedTimestamp;
            snap.PhaseJobStarts[p] = new long[_phases[p].Length];
            snap.PhaseJobEnds[p] = new long[_phases[p].Length];
            for (var i = 0; i < _phases[p].Length; i++)
            {
                snap.PhaseJobStarts[p][i] = _phases[p][i].StartTimestamp;
                snap.PhaseJobEnds[p][i] = _phases[p][i].EndTimestamp;
            }
        }

        return snap;
    }

    private static void PrintAnalysis(RawSnapshot[] snapshots)
    {
        var freq = (double)Stopwatch.Frequency;
        static double ToUs(long from, long to, double f) => (to - from) / f * 1_000_000;

        var records = new TickRecord[snapshots.Length];
        for (var t = 0; t < snapshots.Length; t++)
        {
            ref var s = ref snapshots[t];
            var rec = new TickRecord
            {
                TotalUs = ToUs(s.SubmitTs, s.DagCompleteTs, freq),
                PreTriggerUs = ToUs(s.SubmitTs, s.TriggerTs, freq),
                PhaseSpanUs = new double[PhaseCount],
                PhaseWorkUs = new double[PhaseCount],
                PrePhaseUs = new double[PhaseCount],
                PostPhaseUs = new double[PhaseCount],
            };

            for (var p = 0; p < PhaseCount; p++)
            {
                var pStart = Min(s.PhaseJobStarts[p]);
                var pEnd = Max(s.PhaseJobEnds[p]);
                rec.PhaseSpanUs[p] = ToUs(pStart, pEnd, freq);
                rec.PhaseWorkUs[p] = SumDurations(s.PhaseJobStarts[p], s.PhaseJobEnds[p], freq);

                var preceding = p == 0 ? s.TriggerTs : s.BarrierTs[p - 1];
                rec.PrePhaseUs[p] = ToUs(preceding, pStart, freq);
                rec.PostPhaseUs[p] = ToUs(pEnd, s.BarrierTs[p], freq);
            }

            rec.PostFinalUs = ToUs(s.BarrierTs[^1], s.DagCompleteTs, freq);
            records[t] = rec;
        }

        Array.Sort(records, (a, b) => a.TotalUs.CompareTo(b.TotalUs));

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  PHASE BREAKDOWN ANALYSIS");
        Console.WriteLine($"  {records.Length} ticks, {PhaseCount} phases × {JobsPerPhase} jobs, sorted by total time");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        var percentiles = new[] { ("P1", 0.01), ("P10", 0.10), ("P50", 0.50), ("P90", 0.90), ("P99", 0.99) };

        Console.WriteLine();
        Console.WriteLine("  ┌─────────────────────────────┬────────┬────────┬────────┬────────┬────────┐");
        Console.WriteLine("  │ Segment                     │   P1   │  P10   │  P50   │  P90   │  P99   │");
        Console.WriteLine("  ├─────────────────────────────┼────────┼────────┼────────┼────────┼────────┤");

        PrintRow("Total tick", records, percentiles, r => r.TotalUs);
        PrintRow("Pre-trigger", records, percentiles, r => r.PreTriggerUs);
        for (var p = 0; p < PhaseCount; p++)
        {
            var pi = p;
            PrintRow($"→ P{p + 1} first job", records, percentiles, r => r.PrePhaseUs[pi]);
            PrintRow($"P{p + 1} span ({JobsPerPhase} jobs)", records, percentiles, r => r.PhaseSpanUs[pi]);
            PrintRow($"P{p + 1} last → barrier", records, percentiles, r => r.PostPhaseUs[pi]);
        }

        PrintRow("Post-final barrier", records, percentiles, r => r.PostFinalUs);

        Console.WriteLine("  └─────────────────────────────┴────────┴────────┴────────┴────────┴────────┘");

        Console.WriteLine();
        Console.WriteLine("  PARALLELISM EFFICIENCY (ideal = total_work / (span × cores))");
        Console.WriteLine("  ┌─────────────────────────────┬────────┬────────┬────────┐");
        Console.WriteLine("  │ Phase                       │ P50 Sp │  Work  │  Eff%  │");
        Console.WriteLine("  ├─────────────────────────────┼────────┼────────┼────────┤");

        for (var p = 0; p < PhaseCount; p++)
        {
            var pi = p;
            PrintEfficiency($"P{p + 1} ({JobsPerPhase} jobs / {Cores} cores)", records, r => r.PhaseSpanUs[pi], r => r.PhaseWorkUs[pi], Cores);
        }

        Console.WriteLine("  └─────────────────────────────┴────────┴────────┴────────┘");

        var p50Idx = (int)(records.Length * 0.50);
        var r50 = records[p50Idx];

        var totalSpanUs = 0.0;
        var totalWorkUs = 0.0;
        var totalOverheadUs = r50.PreTriggerUs + r50.PostFinalUs;
        for (var p = 0; p < PhaseCount; p++)
        {
            totalSpanUs += r50.PhaseSpanUs[p];
            totalWorkUs += r50.PhaseWorkUs[p];
            totalOverheadUs += r50.PrePhaseUs[p] + r50.PostPhaseUs[p];
        }

        var optimalUs = totalWorkUs / Cores;
        var gapUs = r50.TotalUs - optimalUs;

        Console.WriteLine();
        Console.WriteLine($"  P50 total: {r50.TotalUs:F1} μs  =  {totalSpanUs:F1} μs phases  +  {totalOverheadUs:F1} μs overhead ({totalOverheadUs / r50.TotalUs * 100:F1}%)");
        Console.WriteLine($"  Theoretical optimal (total job work / {Cores} cores): {optimalUs:F1} μs");
        Console.WriteLine($"  Gap to optimal: {gapUs:F1} μs  ({gapUs / optimalUs * 100:F1}% above optimal)");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
    }

    private static long Min(long[] values)
    {
        var min = long.MaxValue;
        foreach (var v in values) if (v < min) min = v;
        return min;
    }

    private static long Max(long[] values)
    {
        var max = long.MinValue;
        foreach (var v in values) if (v > max) max = v;
        return max;
    }

    private static double SumDurations(long[] starts, long[] ends, double freq)
    {
        var sum = 0.0;
        for (var i = 0; i < starts.Length; i++)
            sum += (ends[i] - starts[i]) / freq * 1_000_000;
        return sum;
    }

    private static void PrintRow(
        string label, TickRecord[] records,
        (string Name, double Pct)[] percentiles, Func<TickRecord, double> selector)
    {
        var values = new double[percentiles.Length];
        for (var i = 0; i < percentiles.Length; i++)
            values[i] = selector(records[(int)(records.Length * percentiles[i].Pct)]);

        Console.Write($"  │ {label,-27} │");
        foreach (var v in values)
            Console.Write($" {v,6:F1} │");
        Console.WriteLine();
    }

    private static void PrintEfficiency(
        string label, TickRecord[] records,
        Func<TickRecord, double> spanSelector, Func<TickRecord, double> workSelector, int cores)
    {
        var p50Idx = (int)(records.Length * 0.50);
        var span = spanSelector(records[p50Idx]);
        var work = workSelector(records[p50Idx]);
        var efficiency = span > 0 ? work / (span * cores) * 100 : 0;

        Console.Write($"  │ {label,-27} │ {span,6:F1} │ {work,6:F0} │ {efficiency,5:F1}% │");
        Console.WriteLine();
    }

    private struct RawSnapshot
    {
        public long SubmitTs, DagCompleteTs;
        public long TriggerTs;
        public long[] BarrierTs;
        public long[][] PhaseJobStarts, PhaseJobEnds;
    }

    private struct TickRecord
    {
        public double TotalUs;
        public double PreTriggerUs;
        public double[] PhaseSpanUs;
        public double[] PhaseWorkUs;
        public double[] PrePhaseUs;
        public double[] PostPhaseUs;
        public double PostFinalUs;
    }

    // ── Scheduler instrumentation analysis ──────────────────────────────

    private void RunSchedulerAnalysis()
    {
        const int AnalysisTicks = 2000;
        var accessor = _pool.GetTestAccessor();

        accessor.EnableInstrumentation(maxEventsPerWorker: 1024);

        var perTickRecords = new SchedulerRecord[AnalysisTicks][];
        for (var t = 0; t < AnalysisTicks; t++)
        {
            accessor.ResetInstrumentation();
            this.WireAndSubmit();
            perTickRecords[t] = accessor.GetInstrumentationRecords();
        }

        accessor.DisableInstrumentation();
        PrintSchedulerAnalysis(perTickRecords, accessor.WorkerCount);
    }

    private static void PrintSchedulerAnalysis(SchedulerRecord[][] perTickRecords, int workerCount)
    {
        var freq = (double)Stopwatch.Frequency;
        static double ToUs(long ticks) => ticks / (double)Stopwatch.Frequency * 1_000_000;

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  SCHEDULER INSTRUMENTATION ANALYSIS");
        Console.WriteLine($"  {perTickRecords.Length} ticks, {workerCount} workers");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        // Aggregate across all ticks: per-worker stats
        var workerLocal = new long[workerCount];
        var workerSteal = new long[workerCount];
        var workerInjection = new long[workerCount];
        var workerTotalIdleTicks = new long[workerCount];
        var workerTotalExecTicks = new long[workerCount];
        var workerJobCount = new long[workerCount];
        var workerMaxIdleTicks = new long[workerCount];

        var allReadiedCounts = new List<int>();

        foreach (var tickRecords in perTickRecords)
        {
            // Group by worker to compute idle gaps
            var perWorker = new Dictionary<int, List<SchedulerRecord>>();
            foreach (var rec in tickRecords)
            {
                if (!perWorker.TryGetValue(rec.WorkerIndex, out var list))
                    perWorker[rec.WorkerIndex] = list = [];
                list.Add(rec);
            }

            foreach (var (workerIdx, records) in perWorker)
            {
                if (workerIdx >= workerCount) continue;

                for (var i = 0; i < records.Count; i++)
                {
                    var rec = records[i];
                    var execTicks = rec.CompletedTs - rec.ObtainedTs;
                    workerTotalExecTicks[workerIdx] += execTicks;
                    workerJobCount[workerIdx]++;

                    switch (rec.Source)
                    {
                        case JobSource.Local: workerLocal[workerIdx]++; break;
                        case JobSource.Steal: workerSteal[workerIdx]++; break;
                        case JobSource.Injection: workerInjection[workerIdx]++; break;
                    }

                    if (rec.ReadiedCount > 0)
                        allReadiedCounts.Add(rec.ReadiedCount);

                    if (i > 0)
                    {
                        var idleTicks = rec.ObtainedTs - records[i - 1].CompletedTs;
                        if (idleTicks > 0)
                        {
                            workerTotalIdleTicks[workerIdx] += idleTicks;
                            if (idleTicks > workerMaxIdleTicks[workerIdx])
                                workerMaxIdleTicks[workerIdx] = idleTicks;
                        }
                    }
                }
            }
        }

        // Per-worker table
        Console.WriteLine();
        Console.WriteLine("  ┌────────┬────────┬──────────────────────────┬──────────┬──────────┬──────────┐");
        Console.WriteLine("  │ Worker │  Jobs  │  Source (L / S / I)      │ Idle/job │ Max idle │ Exec/job │");
        Console.WriteLine("  ├────────┼────────┼──────────────────────────┼──────────┼──────────┼──────────┤");

        long totalJobs = 0, totalLocal = 0, totalSteal = 0, totalInjection = 0;
        double totalIdleUs = 0, totalExecUs = 0;

        for (var w = 0; w < workerCount; w++)
        {
            var jobs = workerJobCount[w];
            if (jobs == 0) continue;

            var local = workerLocal[w];
            var steal = workerSteal[w];
            var injection = workerInjection[w];
            var avgIdleUs = ToUs(workerTotalIdleTicks[w]) / jobs;
            var maxIdleUs = ToUs(workerMaxIdleTicks[w]);
            var avgExecUs = ToUs(workerTotalExecTicks[w]) / jobs;

            totalJobs += jobs;
            totalLocal += local;
            totalSteal += steal;
            totalInjection += injection;
            totalIdleUs += ToUs(workerTotalIdleTicks[w]);
            totalExecUs += ToUs(workerTotalExecTicks[w]);

            Console.WriteLine($"  │ {w,6} │ {jobs / perTickRecords.Length,6} │ {local / perTickRecords.Length,6} / {steal / perTickRecords.Length,6} / {injection / perTickRecords.Length,6} │ {avgIdleUs,7:F2}μ │ {maxIdleUs,7:F1}μ │ {avgExecUs,7:F2}μ │");
        }

        Console.WriteLine("  └────────┴────────┴──────────────────────────┴──────────┴──────────┴──────────┘");

        var ticks = (double)perTickRecords.Length;
        Console.WriteLine();
        Console.WriteLine($"  Avg per tick: {totalJobs / ticks:F0} jobs  ({totalLocal / ticks:F0} local, {totalSteal / ticks:F0} steal, {totalInjection / ticks:F0} injection)");
        Console.WriteLine($"  Avg scheduling overhead: {totalIdleUs / totalJobs * 1000:F0} ns/job  (total idle: {totalIdleUs / ticks:F1} μs/tick)");
        Console.WriteLine($"  Avg execute+propagate:   {totalExecUs / totalJobs * 1000:F0} ns/job");

        if (allReadiedCounts.Count > 0)
        {
            allReadiedCounts.Sort();
            var maxReadied = allReadiedCounts[^1];
            var p50Readied = allReadiedCounts[allReadiedCounts.Count / 2];
            var p99Readied = allReadiedCounts[(int)(allReadiedCounts.Count * 0.99)];

            // Histogram of fan-out sizes
            var buckets = new int[6]; // 1, 2-4, 5-16, 17-64, 65-128, 129+
            foreach (var r in allReadiedCounts)
            {
                if (r <= 1) buckets[0]++;
                else if (r <= 4) buckets[1]++;
                else if (r <= 16) buckets[2]++;
                else if (r <= 64) buckets[3]++;
                else if (r <= 128) buckets[4]++;
                else buckets[5]++;
            }

            Console.WriteLine();
            Console.WriteLine($"  Fan-out (readied > 0): {allReadiedCounts.Count} events, P50={p50Readied}, P99={p99Readied}, max={maxReadied}");
            Console.WriteLine($"    1: {buckets[0]}  2-4: {buckets[1]}  5-16: {buckets[2]}  17-64: {buckets[3]}  65-128: {buckets[4]}  129+: {buckets[5]}");
        }

        Console.WriteLine("═══════════════════════════════════════════════════════════════");
    }

    // ── Job allocation (once) ────────────────────────────────────────────

    private void AllocateJobs()
    {
        _trigger = new TriggerJob();

        _phases = new ComputeJob[PhaseCount][];
        _barriers = new BarrierJob[PhaseCount];

        for (var p = 0; p < PhaseCount; p++)
        {
            _phases[p] = new ComputeJob[JobsPerPhase];
            for (var i = 0; i < JobsPerPhase; i++)
                _phases[p][i] = new ComputeJob();
            _barriers[p] = new BarrierJob();
        }
    }

    // ── Per-tick wiring ──────────────────────────────────────────────────

    private void WireAndSubmit()
    {
        _trigger.Reset();

        for (var p = 0; p < PhaseCount; p++)
        {
            var predecessor = p == 0 ? (Job)_trigger : _barriers[p - 1];
            var phase = _phases[p];

            for (var i = 0; i < phase.Length; i++)
            {
                var job = phase[i];
                job.Reset();
                job.Iterations = ComputeIterations;
                job.Seed = (p * JobsPerPhase) + i;
                job.DependsOn(predecessor);
            }

            _barriers[p].Reset();
            for (var i = 0; i < phase.Length; i++)
                _barriers[p].DependsOn(phase[i]);
        }

        _scheduler.Submit(_trigger);
    }
}
