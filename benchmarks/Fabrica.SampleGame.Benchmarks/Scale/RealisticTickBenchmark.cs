using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.SampleGame.Benchmarks.Scale;

namespace Fabrica.SampleGame.Benchmarks;

/// <summary>
/// Models a realistic game tick: 4 sequential phases with parallel fan-out within each phase.
/// Each job does ~15-20μs of real CPU work (hash-mixing), and the final phase also allocates
/// nodes through the arena system. Job counts are multiples of 12 (P-core count on Apple M4 Max)
/// to eliminate ceil-division rounding waste and isolate true scheduler overhead.
///
///   Phase 1 (AI/Decision):     192 ComputeJobs  (16 per core)
///   Phase 2 (Physics):         192 ComputeJobs  (16 per core)
///   Phase 3 (World Update):     96 ComputeJobs  ( 8 per core)
///   Phase 4 (Snapshot):         48 SnapshotJobs  ( 4 per core) + 1 SnapshotCollectorJob
///
/// Total: ~530 jobs. Every phase divides evenly across 12 cores — no straggler waste.
/// </summary>
[MemoryDiagnoser]
[EventPipeProfiler(EventPipeProfile.CpuSampling)]
public class RealisticTickBenchmark
{
    private const int Phase1Count = 192;
    private const int Phase2Count = 192;
    private const int Phase3Count = 96;
    private const int Phase4Count = 48;
    private const int SnapshotNodeCount = 10;

    /// <summary>
    /// Hash-mixing iterations per job. Calibrate so each job takes ~15-20μs.
    /// Halved from 50k (with 2× job counts) to keep total work per phase constant while giving
    /// every phase a clean multiple of 12 cores — eliminating ceil-division rounding waste.
    /// </summary>
    private const int ComputeIterations = 25_000;

    private WorkerPool _pool = null!;
    private JobScheduler _scheduler = null!;
    private GlobalNodeStore<BenchNode, BenchNodeOps> _store = null!;
    private MergeCoordinator _coordinator;

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
        Console.WriteLine($"UNSAFE_OPT (BuildConfig.UnsafeOptimizations): {Fabrica.Core.BuildConfig.UnsafeOptimizations}");

        _pool = new WorkerPool(coordinatorCount: 1);
        _scheduler = new JobScheduler(_pool);
        _store = new GlobalNodeStore<BenchNode, BenchNodeOps>(_pool.WorkerCount);
        var ops = new BenchNodeOps { Store = _store };
        _store.SetNodeOps(ops);
        _coordinator = new MergeCoordinator([_store]);

        this.AllocateJobs();

        for (var i = 0; i < 10; i++)
            this.RunOneTick();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        this.RunPhaseAnalysis();
        this.RunSchedulerAnalysis();

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

            SnapshotSlice<BenchNode, BenchNodeOps> slice;
            using (var merge = _coordinator.MergeAll())
                slice = _store.BuildSnapshotSlice();

            if (_hasPreviousSlice)
                _store.ReleaseSnapshotSlice(_previousSlice);
            _previousSlice = slice;
            _hasPreviousSlice = true;

            snapshots[t] = this.CaptureRawSnapshot(submitTs, dagCompleteTs);
        }

        this.SetInstrument(false);
        PrintAnalysis(snapshots);
    }

    private void SetInstrument(bool enabled)
    {
        _trigger.Instrument = enabled;
        _barrier1.Instrument = enabled;
        _barrier2.Instrument = enabled;
        _barrier3.Instrument = enabled;
        _collector.Instrument = enabled;
        foreach (var j in _phase1) j.Instrument = enabled;
        foreach (var j in _phase2) j.Instrument = enabled;
        foreach (var j in _phase3) j.Instrument = enabled;
        foreach (var j in _phase4) j.Instrument = enabled;
    }

    private RawSnapshot CaptureRawSnapshot(long submitTs, long dagCompleteTs)
    {
        var snap = new RawSnapshot
        {
            SubmitTs = submitTs,
            DagCompleteTs = dagCompleteTs,
            TriggerTs = _trigger.ExecutedTimestamp,
            B1Ts = _barrier1.ExecutedTimestamp,
            B2Ts = _barrier2.ExecutedTimestamp,
            B3Ts = _barrier3.ExecutedTimestamp,
            CollStartTs = _collector.StartTimestamp,
            CollEndTs = _collector.EndTimestamp,
            P1JobStarts = new long[_phase1.Length],
            P1JobEnds = new long[_phase1.Length],
            P2JobStarts = new long[_phase2.Length],
            P2JobEnds = new long[_phase2.Length],
            P3JobStarts = new long[_phase3.Length],
            P3JobEnds = new long[_phase3.Length],
            P4JobStarts = new long[_phase4.Length],
            P4JobEnds = new long[_phase4.Length],
        };

        for (var i = 0; i < _phase1.Length; i++) { snap.P1JobStarts[i] = _phase1[i].StartTimestamp; snap.P1JobEnds[i] = _phase1[i].EndTimestamp; }
        for (var i = 0; i < _phase2.Length; i++) { snap.P2JobStarts[i] = _phase2[i].StartTimestamp; snap.P2JobEnds[i] = _phase2[i].EndTimestamp; }
        for (var i = 0; i < _phase3.Length; i++) { snap.P3JobStarts[i] = _phase3[i].StartTimestamp; snap.P3JobEnds[i] = _phase3[i].EndTimestamp; }
        for (var i = 0; i < _phase4.Length; i++) { snap.P4JobStarts[i] = _phase4[i].StartTimestamp; snap.P4JobEnds[i] = _phase4[i].EndTimestamp; }

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

            var p1Start = Min(s.P1JobStarts);
            var p1End = Max(s.P1JobEnds);
            var p2Start = Min(s.P2JobStarts);
            var p2End = Max(s.P2JobEnds);
            var p3Start = Min(s.P3JobStarts);
            var p3End = Max(s.P3JobEnds);
            var p4Start = Min(s.P4JobStarts);
            var p4End = Max(s.P4JobEnds);

            var p1Work = SumDurations(s.P1JobStarts, s.P1JobEnds, freq);
            var p2Work = SumDurations(s.P2JobStarts, s.P2JobEnds, freq);
            var p3Work = SumDurations(s.P3JobStarts, s.P3JobEnds, freq);
            var p4Work = SumDurations(s.P4JobStarts, s.P4JobEnds, freq);

            records[t] = new TickRecord
            {
                TotalUs = ToUs(s.SubmitTs, s.DagCompleteTs, freq),
                PreTriggerUs = ToUs(s.SubmitTs, s.TriggerTs, freq),
                TriggerToP1FirstUs = ToUs(s.TriggerTs, p1Start, freq),
                P1SpanUs = ToUs(p1Start, p1End, freq),
                P1EndToB1Us = ToUs(p1End, s.B1Ts, freq),
                B1ToP2FirstUs = ToUs(s.B1Ts, p2Start, freq),
                P2SpanUs = ToUs(p2Start, p2End, freq),
                P2EndToB2Us = ToUs(p2End, s.B2Ts, freq),
                B2ToP3FirstUs = ToUs(s.B2Ts, p3Start, freq),
                P3SpanUs = ToUs(p3Start, p3End, freq),
                P3EndToB3Us = ToUs(p3End, s.B3Ts, freq),
                B3ToP4FirstUs = ToUs(s.B3Ts, p4Start, freq),
                P4SpanUs = ToUs(p4Start, p4End, freq),
                P4EndToCollStartUs = ToUs(p4End, s.CollStartTs, freq),
                CollectorUs = ToUs(s.CollStartTs, s.CollEndTs, freq),
                PostCollectorUs = ToUs(s.CollEndTs, s.DagCompleteTs, freq),
                P1TotalWorkUs = p1Work,
                P2TotalWorkUs = p2Work,
                P3TotalWorkUs = p3Work,
                P4TotalWorkUs = p4Work,
            };
        }

        Array.Sort(records, (a, b) => a.TotalUs.CompareTo(b.TotalUs));

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  PHASE BREAKDOWN ANALYSIS");
        Console.WriteLine($"  {records.Length} ticks, sorted by total time");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        var percentiles = new[] { ("P1", 0.01), ("P10", 0.10), ("P50", 0.50), ("P90", 0.90), ("P99", 0.99) };

        Console.WriteLine();
        Console.WriteLine("  ┌─────────────────────────────┬────────┬────────┬────────┬────────┬────────┐");
        Console.WriteLine("  │ Segment                     │   P1   │  P10   │  P50   │  P90   │  P99   │");
        Console.WriteLine("  ├─────────────────────────────┼────────┼────────┼────────┼────────┼────────┤");

        PrintRow("Total tick", records, percentiles, r => r.TotalUs);
        PrintRow("Pre-trigger", records, percentiles, r => r.PreTriggerUs);
        PrintRow("Trigger→P1 first", records, percentiles, r => r.TriggerToP1FirstUs);
        PrintRow("P1 span (192 jobs)", records, percentiles, r => r.P1SpanUs);
        PrintRow("P1 last→Barrier1", records, percentiles, r => r.P1EndToB1Us);
        PrintRow("Barrier1→P2 first", records, percentiles, r => r.B1ToP2FirstUs);
        PrintRow("P2 span (192 jobs)", records, percentiles, r => r.P2SpanUs);
        PrintRow("P2 last→Barrier2", records, percentiles, r => r.P2EndToB2Us);
        PrintRow("Barrier2→P3 first", records, percentiles, r => r.B2ToP3FirstUs);
        PrintRow("P3 span (96 jobs)", records, percentiles, r => r.P3SpanUs);
        PrintRow("P3 last→Barrier3", records, percentiles, r => r.P3EndToB3Us);
        PrintRow("Barrier3→P4 first", records, percentiles, r => r.B3ToP4FirstUs);
        PrintRow("P4 span (48 snap)", records, percentiles, r => r.P4SpanUs);
        PrintRow("P4 last→Coll start", records, percentiles, r => r.P4EndToCollStartUs);
        PrintRow("Collector exec", records, percentiles, r => r.CollectorUs);
        PrintRow("Post-collector", records, percentiles, r => r.PostCollectorUs);

        Console.WriteLine("  └─────────────────────────────┴────────┴────────┴────────┴────────┴────────┘");

        Console.WriteLine();
        Console.WriteLine("  PARALLELISM EFFICIENCY (ideal = total_work / (span × cores))");
        Console.WriteLine("  ┌─────────────────────────────┬────────┬────────┬────────┐");
        Console.WriteLine("  │ Phase                       │ P50 Sp │  Work  │  Eff%  │");
        Console.WriteLine("  ├─────────────────────────────┼────────┼────────┼────────┤");

        PrintEfficiency("P1 (192 jobs / 12 cores)", records, r => r.P1SpanUs, r => r.P1TotalWorkUs, 12);
        PrintEfficiency("P2 (192 jobs / 12 cores)", records, r => r.P2SpanUs, r => r.P2TotalWorkUs, 12);
        PrintEfficiency("P3 (96 jobs / 12 cores)", records, r => r.P3SpanUs, r => r.P3TotalWorkUs, 12);
        PrintEfficiency("P4 (48 snap / 12 cores)", records, r => r.P4SpanUs, r => r.P4TotalWorkUs, 12);

        Console.WriteLine("  └─────────────────────────────┴────────┴────────┴────────┘");

        const int Cores = 12;
        var p50Idx = (int)(records.Length * 0.50);
        var r50 = records[p50Idx];
        var overheadUs = r50.PreTriggerUs + r50.TriggerToP1FirstUs + r50.P1EndToB1Us
            + r50.B1ToP2FirstUs + r50.P2EndToB2Us + r50.B2ToP3FirstUs
            + r50.P3EndToB3Us + r50.B3ToP4FirstUs + r50.P4EndToCollStartUs
            + r50.CollectorUs + r50.PostCollectorUs;
        var workUs = r50.P1SpanUs + r50.P2SpanUs + r50.P3SpanUs + r50.P4SpanUs;

        var totalJobWorkUs = r50.P1TotalWorkUs + r50.P2TotalWorkUs + r50.P3TotalWorkUs + r50.P4TotalWorkUs;
        var optimalUs = totalJobWorkUs / Cores;
        var gapUs = r50.TotalUs - optimalUs;

        Console.WriteLine();
        Console.WriteLine($"  P50 total: {r50.TotalUs:F1} μs  =  {workUs:F1} μs work  +  {overheadUs:F1} μs overhead ({overheadUs / r50.TotalUs * 100:F1}%)");
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
        public long TriggerTs, B1Ts, B2Ts, B3Ts;
        public long CollStartTs, CollEndTs;
        public long[] P1JobStarts, P1JobEnds;
        public long[] P2JobStarts, P2JobEnds;
        public long[] P3JobStarts, P3JobEnds;
        public long[] P4JobStarts, P4JobEnds;
    }

    private struct TickRecord
    {
        public double TotalUs;
        public double PreTriggerUs;
        public double TriggerToP1FirstUs;
        public double P1SpanUs;
        public double P1EndToB1Us;
        public double B1ToP2FirstUs;
        public double P2SpanUs;
        public double P2EndToB2Us;
        public double B2ToP3FirstUs;
        public double P3SpanUs;
        public double P3EndToB3Us;
        public double B3ToP4FirstUs;
        public double P4SpanUs;
        public double P4EndToCollStartUs;
        public double CollectorUs;
        public double PostCollectorUs;
        public double P1TotalWorkUs;
        public double P2TotalWorkUs;
        public double P3TotalWorkUs;
        public double P4TotalWorkUs;
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

            SnapshotSlice<BenchNode, BenchNodeOps> slice;
            using (var merge = _coordinator.MergeAll())
                slice = _store.BuildSnapshotSlice();

            if (_hasPreviousSlice)
                _store.ReleaseSnapshotSlice(_previousSlice);
            _previousSlice = slice;
            _hasPreviousSlice = true;

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
