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
    public void DeferredConsumerPinning_AcrossThreadBoundaries()
    {
        var metrics = new TestStressMetrics();
        var saveMetrics = new TestSaveMetrics();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var simulator = new SimulationCoordinator(Math.Max(1, Environment.ProcessorCount - 1));

        RunEngineWithDeferredSave(metrics, saveMetrics, simulator, cancellationSource.Token);

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
        var nodePool = new ObjectPool<ChainNode<WorldImage>>(SimulationConstants.SnapshotPoolSize);
        var imagePool = new ObjectPool<WorldImage>(SimulationConstants.SnapshotPoolSize);
        var pinnedVersions = new PinnedVersions();
        var shared = new SharedState<WorldImage>();
        var producer = new SimulationProducer(imagePool, simulator);
        var consumer = new TestInvariantCheckingConsumer(metrics, renderDelayMilliseconds);
        var clock = new TestStressClock();

        var productionLoop = new ProductionLoop<WorldImage, SimulationProducer, TestStressClock, ThreadWaiter>(
            nodePool, pinnedVersions, shared, producer, clock, new ThreadWaiter());
        var consumptionLoop = new ConsumptionLoop<WorldImage, TestInvariantCheckingConsumer, TestStressClock, ThreadWaiter>(
            pinnedVersions, shared, consumer, clock, new ThreadWaiter(), []);

        RunBothLoops(productionLoop, consumptionLoop, simulator, cancellationToken, metrics);
    }

    private static void RunEngineWithDeferredSave(
        TestStressMetrics metrics,
        TestSaveMetrics saveMetrics,
        SimulationCoordinator simulator,
        CancellationToken cancellationToken)
    {
        var nodePool = new ObjectPool<ChainNode<WorldImage>>(SimulationConstants.SnapshotPoolSize);
        var imagePool = new ObjectPool<WorldImage>(SimulationConstants.SnapshotPoolSize);
        var pinnedVersions = new PinnedVersions();
        var shared = new SharedState<WorldImage>();
        var producer = new SimulationProducer(imagePool, simulator);
        var consumer = new TestInvariantCheckingConsumer(metrics, renderDelayMilliseconds: 0);
        var clock = new TestStressClock();

        var saveConsumer = new TestSlowDeferredSaveConsumer(saveMetrics);
        var deferredConsumers = new DeferredConsumerRegistration<WorldImage>[]
        {
            new(saveConsumer, 0L),
        };

        var productionLoop = new ProductionLoop<WorldImage, SimulationProducer, TestStressClock, ThreadWaiter>(
            nodePool, pinnedVersions, shared, producer, clock, new ThreadWaiter());
        var consumptionLoop = new ConsumptionLoop<WorldImage, TestInvariantCheckingConsumer, TestStressClock, ThreadWaiter>(
            pinnedVersions, shared, consumer, clock, new ThreadWaiter(), deferredConsumers);

        RunBothLoops(productionLoop, consumptionLoop, simulator, cancellationToken, metrics);
    }

    private static void RunBothLoops<TConsumer>(
        ProductionLoop<WorldImage, SimulationProducer, TestStressClock, ThreadWaiter> productionLoop,
        ConsumptionLoop<WorldImage, TConsumer, TestStressClock, ThreadWaiter> consumptionLoop,
        SimulationCoordinator simulator,
        CancellationToken cancellationToken,
        TestStressMetrics metrics)
        where TConsumer : struct, IConsumer<WorldImage>
    {
        Exception? simulationException = null;
        Exception? consumptionException = null;

        var simulationThread = new Thread(() =>
        {
            try { productionLoop.Run(cancellationToken); }
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

    private struct TestInvariantCheckingConsumer : IConsumer<WorldImage>
    {
        private readonly TestStressMetrics _metrics;
        private readonly int _renderDelayMilliseconds;

        public TestInvariantCheckingConsumer(TestStressMetrics metrics, int renderDelayMilliseconds)
        {
            _metrics = metrics;
            _renderDelayMilliseconds = renderDelayMilliseconds;
        }

        public void Consume(ChainNode<WorldImage>? previous, ChainNode<WorldImage> latest, long frameStartNanoseconds, CancellationToken cancellationToken)
        {
            _metrics.RecordFrame(previous, latest);
            if (_renderDelayMilliseconds > 0)
                Thread.Sleep(_renderDelayMilliseconds);
        }
    }

    private sealed class TestSlowDeferredSaveConsumer : IDeferredConsumer<WorldImage>
    {
        private readonly TestSaveMetrics _metrics;

        public TestSlowDeferredSaveConsumer(TestSaveMetrics metrics) => _metrics = metrics;

        public Task<long> ConsumeAsync(WorldImage payload, int sequenceNumber, CancellationToken cancellationToken) =>
            Task.Run<long>(() =>
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

                var ticks = Stopwatch.GetTimestamp();
                var seconds = ticks / Stopwatch.Frequency;
                var remainder = ticks % Stopwatch.Frequency;
                var now = seconds * 1_000_000_000L + remainder * 1_000_000_000L / Stopwatch.Frequency;
                return now + 100_000_000L;
            }, cancellationToken);
    }

    // ── Metrics ──────────────────────────────────────────────────────────────

    private sealed class TestStressMetrics
    {
        private long _framesRendered;
        private int _maxTickObserved;
        private int _lastLatestTick = -1;
        private volatile string? _invariantViolation;

        public long FramesRendered => Volatile.Read(ref _framesRendered);
        public int MaxTickObserved => Volatile.Read(ref _maxTickObserved);
        public string? InvariantViolation => _invariantViolation;

        public void RecordFrame(ChainNode<WorldImage>? previous, ChainNode<WorldImage> latest)
        {
            Interlocked.Increment(ref _framesRendered);

            var latestTick = latest.SequenceNumber;

            var previousLatestTick = _lastLatestTick;
            if (latestTick < previousLatestTick)
            {
                _invariantViolation ??= $"Latest tick went backwards: {latestTick} < {previousLatestTick}";
                return;
            }
            _lastLatestTick = latestTick;

            if (previous is not null && previous.SequenceNumber > latestTick)
            {
                _invariantViolation ??= $"Previous tick ({previous.SequenceNumber}) > latest tick ({latestTick})";
                return;
            }

            UpdateMax(ref _maxTickObserved, latestTick);
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
