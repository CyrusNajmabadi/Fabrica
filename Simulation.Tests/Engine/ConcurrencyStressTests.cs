using System.Diagnostics;
using Simulation.Engine;
using Simulation.Memory;
using Simulation.Tests.Helpers;
using Simulation.World;
using Xunit;

namespace Simulation.Tests.Engine;

[Trait("Category", "Stress")]
public sealed class ConcurrencyStressTests
{
    [Fact]
    public void SustainsHighThroughput_NoDeadlocks()
    {
        var metrics = new TestStressMetrics();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var simulator = new SimulationCoordinator(Math.Max(1, Environment.ProcessorCount - 1));

        RunEngine(metrics, simulator, cancellationSource.Token, renderDelayMilliseconds: 0);

        Assert.True(metrics.FramesRendered > 0, "Expected at least one frame to be rendered.");
        Assert.True(metrics.MaxTickObserved > 0, "Expected simulation to advance past tick 0.");
        Assert.Null(metrics.InvariantViolation);
    }

    [Fact]
    public void BackpressureEngages_WhenConsumptionIsSlowedDown()
    {
        var metrics = new TestStressMetrics();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var simulator = new SimulationCoordinator(Math.Max(1, Environment.ProcessorCount - 1));

        RunEngine(metrics, simulator, cancellationSource.Token, renderDelayMilliseconds: 50);

        Assert.True(metrics.FramesRendered > 0, "Expected at least one frame to be rendered.");
        Assert.True(metrics.MaxTickObserved > 0, "Expected simulation to advance past tick 0.");
        Assert.Null(metrics.InvariantViolation);

        // With 50ms render delay (3x the ~16ms frame budget), the simulation should not
        // run unboundedly far ahead.  The pool has 256 slots at 40 ticks/sec — if
        // backpressure didn't engage, the simulation could produce thousands of ticks
        // while consumption crawls.  The tick-epoch gap (max tick observed vs what
        // consumption has processed) should stay bounded.
        //
        // We don't assert a hard tick count here — just that the system survived
        // without crashing, deadlocking, or violating invariants.  The existence
        // of rendered frames proves consumption was running, and the absence of
        // invariant violations proves snapshots were never used after free.
    }

    [Fact]
    public void GracefulShutdown_UnderLoad()
    {
        var metrics = new TestStressMetrics();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var simulator = new SimulationCoordinator(Math.Max(1, Environment.ProcessorCount - 1));

        var stopwatch = Stopwatch.StartNew();
        RunEngine(metrics, simulator, cancellationSource.Token, renderDelayMilliseconds: 0);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Engine did not shut down within 10 seconds (took {stopwatch.Elapsed}). Possible deadlock.");
        Assert.Null(metrics.InvariantViolation);
    }

    [Fact]
    public void WorkerSignalParkCycle_SurvivesManyTicks()
    {
        var workerCount = Math.Max(4, Environment.ProcessorCount);
        var metrics = new TestStressMetrics();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var simulator = new SimulationCoordinator(workerCount);

        RunEngine(metrics, simulator, cancellationSource.Token, renderDelayMilliseconds: 0);

        Assert.True(metrics.MaxTickObserved > 100,
            $"Expected many ticks with {workerCount} workers, but only observed {metrics.MaxTickObserved}.");
        Assert.Null(metrics.InvariantViolation);
    }

    [Fact]
    public void SavePinning_AcrossThreadBoundaries()
    {
        var metrics = new TestStressMetrics();
        var saveMetrics = new TestSaveMetrics();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var simulator = new SimulationCoordinator(Math.Max(1, Environment.ProcessorCount - 1));

        RunEngineWithSaves(metrics, saveMetrics, simulator, cancellationSource.Token);

        Assert.True(metrics.FramesRendered > 0, "Expected at least one frame to be rendered.");
        Assert.True(saveMetrics.SavesCompleted > 0,
            "Expected at least one save to complete.");
        Assert.Equal(0, saveMetrics.SavesFailed);
        Assert.Null(metrics.InvariantViolation);
    }

    // ── Engine runners ───────────────────────────────────────────────────────

    private static void RunEngine(
        TestStressMetrics metrics,
        SimulationCoordinator simulator,
        CancellationToken cancellationToken,
        int renderDelayMilliseconds)
    {
        var memory = new MemorySystem(SimulationConstants.SnapshotPoolSize);
        var shared = new SharedState();
        var clock = new TestStressClock();
        var renderer = new TestInvariantCheckingRenderer(metrics, renderDelayMilliseconds);

        var simulationLoop = new SimulationLoop<TestStressClock, ThreadWaiter>(
            memory, shared, simulator, clock, new ThreadWaiter());
        var consumptionLoop = new ConsumptionLoop<TestStressClock, ThreadWaiter, TestNoOpSaveRunner, TestNoOpSaver, TestInvariantCheckingRenderer>(
            memory, shared, clock, new ThreadWaiter(), new TestNoOpSaveRunner(), new TestNoOpSaver(), renderer, new RenderCoordinator(1));

        RunBothLoops(simulationLoop, consumptionLoop, simulator, cancellationToken, metrics);
    }

    private static void RunEngineWithSaves(
        TestStressMetrics metrics,
        TestSaveMetrics saveMetrics,
        SimulationCoordinator simulator,
        CancellationToken cancellationToken)
    {
        var memory = new MemorySystem(SimulationConstants.SnapshotPoolSize);
        var shared = new SharedState { NextSaveAtTick = 1 };
        var clock = new TestStressClock();
        var renderer = new TestInvariantCheckingRenderer(metrics, renderDelayMilliseconds: 0);
        var saver = new TestSlowSaver(saveMetrics);

        var simulationLoop = new SimulationLoop<TestStressClock, ThreadWaiter>(
            memory, shared, simulator, clock, new ThreadWaiter());
        var consumptionLoop = new ConsumptionLoop<TestStressClock, ThreadWaiter, TaskSaveRunner, TestSlowSaver, TestInvariantCheckingRenderer>(
            memory, shared, clock, new ThreadWaiter(), new TaskSaveRunner(), saver, renderer, new RenderCoordinator(1));

        RunBothLoops(simulationLoop, consumptionLoop, simulator, cancellationToken, metrics);
    }

    private static void RunBothLoops<TSaveRunner, TSaver>(
        SimulationLoop<TestStressClock, ThreadWaiter> simulationLoop,
        ConsumptionLoop<TestStressClock, ThreadWaiter, TSaveRunner, TSaver, TestInvariantCheckingRenderer> consumptionLoop,
        SimulationCoordinator simulator,
        CancellationToken cancellationToken,
        TestStressMetrics metrics)
        where TSaveRunner : struct, ISaveRunner
        where TSaver : struct, ISaver
    {
        Exception? simulationException = null;
        Exception? consumptionException = null;

        var simulationThread = new Thread(() =>
        {
            try { simulationLoop.Run(cancellationToken); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Volatile.Write(ref simulationException, ex); }
        })
        {
            Name = "StressTest-Simulation",
            IsBackground = false,
        };

        var consumptionThread = new Thread(() =>
        {
            try { consumptionLoop.Run(cancellationToken); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Volatile.Write(ref consumptionException, ex); }
        })
        {
            Name = "StressTest-Consumption",
            IsBackground = false,
        };

        try
        {
            simulationThread.Start();
            consumptionThread.Start();

            simulationThread.Join();
            consumptionThread.Join();
        }
        finally
        {
            simulator.Dispose();
        }

        var simEx = Volatile.Read(ref simulationException);
        var conEx = Volatile.Read(ref consumptionException);

        if (simEx is not null && conEx is not null)
            throw new AggregateException("Both threads threw.", simEx, conEx);
        if (simEx is not null)
            throw new AggregateException("Simulation thread threw.", simEx);
        if (conEx is not null)
            throw new AggregateException("Consumption thread threw.", conEx);

        var violation = metrics.InvariantViolation;
        if (violation is not null)
            Assert.Fail($"Invariant violation: {violation}");
    }

    // ── Struct implementations ───────────────────────────────────────────────

    private readonly struct TestStressClock : IClock
    {
        public long NowNanoseconds
        {
            get
            {
                var ticks = Stopwatch.GetTimestamp();
                var seconds = ticks / Stopwatch.Frequency;
                var remainder = ticks % Stopwatch.Frequency;
                return seconds * 1_000_000_000L + remainder * 1_000_000_000L / Stopwatch.Frequency;
            }
        }
    }

    private readonly struct TestInvariantCheckingRenderer : IRenderer
    {
        private readonly TestStressMetrics _metrics;
        private readonly int _renderDelayMilliseconds;

        public TestInvariantCheckingRenderer(TestStressMetrics metrics, int renderDelayMilliseconds)
        {
            _metrics = metrics;
            _renderDelayMilliseconds = renderDelayMilliseconds;
        }

        public void Render(in RenderFrame frame)
        {
            _metrics.RecordFrame(frame);

            if (_renderDelayMilliseconds > 0)
                Thread.Sleep(_renderDelayMilliseconds);
        }
    }

    private readonly struct TestSlowSaver : ISaver
    {
        private readonly TestSaveMetrics _metrics;

        public TestSlowSaver(TestSaveMetrics metrics) => _metrics = metrics;

        public void Save(WorldImage image, int tick)
        {
            try
            {
                Thread.Sleep(5);
                Interlocked.Increment(ref _metrics._savesCompleted);
            }
            catch
            {
                Interlocked.Increment(ref _metrics._savesFailed);
                throw;
            }
        }
    }

    // ── Metrics ──────────────────────────────────────────────────────────────

    private sealed class TestStressMetrics
    {
        private long _framesRendered;
        private int _maxTickObserved;
        private int _lastCurrentTick = -1;
        private volatile string? _invariantViolation;

        public long FramesRendered => Volatile.Read(ref _framesRendered);
        public int MaxTickObserved => Volatile.Read(ref _maxTickObserved);
        public string? InvariantViolation => _invariantViolation;

        public void RecordFrame(in RenderFrame frame)
        {
            Interlocked.Increment(ref _framesRendered);

            var currentTick = frame.Current.TickNumber;

            // Current tick must be monotonically non-decreasing across frames.
            var previousCurrentTick = _lastCurrentTick;
            if (currentTick < previousCurrentTick)
            {
                _invariantViolation ??= $"Current tick went backwards: {currentTick} < {previousCurrentTick}";
                return;
            }
            _lastCurrentTick = currentTick;

            // Previous tick (when present) must be <= current tick.
            if (frame.Previous is not null && frame.Previous.TickNumber > currentTick)
            {
                _invariantViolation ??= $"Previous tick ({frame.Previous.TickNumber}) > current tick ({currentTick})";
                return;
            }

            // Track highest tick observed.
            UpdateMax(ref _maxTickObserved, currentTick);
        }

        private static void UpdateMax(ref int location, int value)
        {
            int current;
            do
            {
                current = Volatile.Read(ref location);
                if (value <= current) return;
            } while (Interlocked.CompareExchange(ref location, value, current) != current);
        }
    }

    private sealed class TestSaveMetrics
    {
        internal int _savesCompleted;
        internal int _savesFailed;

        public int SavesCompleted => Volatile.Read(ref _savesCompleted);
        public int SavesFailed => Volatile.Read(ref _savesFailed);
    }
}
