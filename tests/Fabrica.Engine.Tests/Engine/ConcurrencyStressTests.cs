using System.Diagnostics;
using Fabrica.Engine.Simulation;
using Fabrica.Engine.Tests.Helpers;
using Fabrica.Engine.World;
using Fabrica.Pipeline;
using Fabrica.Pipeline.Memory;
using Fabrica.Pipeline.Threading;
using Xunit;

namespace Fabrica.Engine.Tests.Engine;

using ChainNode = BaseProductionLoop<WorldImage>.ChainNode;
using ChainNodeAllocator = BaseProductionLoop<WorldImage>.ChainNode.Allocator;

[Trait("Category", "Stress")]
public sealed class ConcurrencyStressTests
{
    [Fact]
    public void SustainsHighThroughput_NoDeadlocks()
    {
        var metrics = new TestStressMetrics();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        RunEngine(metrics, Math.Max(1, Environment.ProcessorCount - 1), cancellationSource.Token, renderDelayMilliseconds: 0);

        Assert.True(metrics.FramesRendered > 0, "Expected at least one frame to be rendered.");
        Assert.True(metrics.MaxTickObserved > 0, "Expected simulation to advance past tick 0.");
        Assert.Null(metrics.InvariantViolation);
    }

    [Fact]
    public void BackpressureEngages_WhenConsumptionIsSlowedDown()
    {
        var metrics = new TestStressMetrics();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        RunEngine(metrics, Math.Max(1, Environment.ProcessorCount - 1), cancellationSource.Token, renderDelayMilliseconds: 50);

        Assert.True(metrics.FramesRendered > 0, "Expected at least one frame to be rendered.");
        Assert.True(metrics.MaxTickObserved > 0, "Expected simulation to advance past tick 0.");
        Assert.Null(metrics.InvariantViolation);
    }

    [Fact]
    public void GracefulShutdown_UnderLoad()
    {
        var metrics = new TestStressMetrics();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var stopwatch = Stopwatch.StartNew();
        RunEngine(metrics, Math.Max(1, Environment.ProcessorCount - 1), cancellationSource.Token, renderDelayMilliseconds: 0);
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

        RunEngine(metrics, workerCount, cancellationSource.Token, renderDelayMilliseconds: 0);

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

        RunEngineWithDeferredSave(metrics, saveMetrics, Math.Max(1, Environment.ProcessorCount - 1), cancellationSource.Token);

        Assert.True(metrics.FramesRendered > 0, "Expected at least one frame to be rendered.");
        Assert.True(saveMetrics.SavesCompleted > 0,
            "Expected at least one save to complete.");
        Assert.Equal(0, saveMetrics.SavesFailed);
        Assert.Null(metrics.InvariantViolation);
    }

    // ── Engine runners ───────────────────────────────────────────────────────

    private static void RunEngine(
        TestStressMetrics metrics,
        int workerCount,
        CancellationToken cancellationToken,
        int renderDelayMilliseconds)
    {
        var nodePool = new ObjectPool<ChainNode, ChainNodeAllocator>(SimulationConstants.SnapshotPoolSize);
        var imagePool = new ObjectPool<WorldImage, WorldImage.Allocator>(SimulationConstants.SnapshotPoolSize);
        var shared = new SharedPipelineState<WorldImage>();
        var producer = new SimulationProducer(imagePool, workerCount);
        var consumer = new TestInvariantCheckingConsumer(metrics, renderDelayMilliseconds);
        var clock = new TestStressClock();

        var productionLoop = new ProductionLoop<WorldImage, SimulationProducer, TestStressClock, ThreadWaiter>(
            nodePool, shared, producer, clock, new ThreadWaiter(), TestPipelineConfiguration.Default);
        var consumptionLoop = new ConsumptionLoop<WorldImage, TestInvariantCheckingConsumer, TestStressClock, ThreadWaiter>(
            shared, consumer, clock, new ThreadWaiter(), [], TestPipelineConfiguration.Default);

        RunBothLoops(productionLoop, consumptionLoop, cancellationToken, metrics);
    }

    private static void RunEngineWithDeferredSave(
        TestStressMetrics metrics,
        TestSaveMetrics saveMetrics,
        int workerCount,
        CancellationToken cancellationToken)
    {
        var nodePool = new ObjectPool<ChainNode, ChainNodeAllocator>(SimulationConstants.SnapshotPoolSize);
        var imagePool = new ObjectPool<WorldImage, WorldImage.Allocator>(SimulationConstants.SnapshotPoolSize);
        var shared = new SharedPipelineState<WorldImage>();
        var producer = new SimulationProducer(imagePool, workerCount);
        var consumer = new TestInvariantCheckingConsumer(metrics, renderDelayMilliseconds: 0);
        var clock = new TestStressClock();

        var saveConsumer = new TestSlowDeferredSaveConsumer(saveMetrics);
        var deferredConsumers = new IDeferredConsumer<WorldImage>[] { saveConsumer };

        var productionLoop = new ProductionLoop<WorldImage, SimulationProducer, TestStressClock, ThreadWaiter>(
            nodePool, shared, producer, clock, new ThreadWaiter(), TestPipelineConfiguration.Default);
        var consumptionLoop = new ConsumptionLoop<WorldImage, TestInvariantCheckingConsumer, TestStressClock, ThreadWaiter>(
            shared, consumer, clock, new ThreadWaiter(), deferredConsumers, TestPipelineConfiguration.Default);

        RunBothLoops(productionLoop, consumptionLoop, cancellationToken, metrics);
    }

    private static void RunBothLoops<TConsumer>(
        ProductionLoop<WorldImage, SimulationProducer, TestStressClock, ThreadWaiter> productionLoop,
        ConsumptionLoop<WorldImage, TConsumer, TestStressClock, ThreadWaiter> consumptionLoop,
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

        simulationThread.Start();
        consumptionThread.Start();

        simulationThread.Join();
        consumptionThread.Join();

        var simulationThreadException = Volatile.Read(ref simulationException);
        var consumptionThreadException = Volatile.Read(ref consumptionException);

        if (simulationThreadException is not null && consumptionThreadException is not null)
            throw new AggregateException("Both threads threw.", simulationThreadException, consumptionThreadException);
        if (simulationThreadException is not null)
            throw new AggregateException("Simulation thread threw.", simulationThreadException);
        if (consumptionThreadException is not null)
            throw new AggregateException("Consumption thread threw.", consumptionThreadException);

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
                return (seconds * 1_000_000_000L) + (remainder * 1_000_000_000L / Stopwatch.Frequency);
            }
        }
    }

    private readonly struct TestInvariantCheckingConsumer(TestStressMetrics metrics, int renderDelayMilliseconds) : IConsumer<WorldImage>
    {
        private readonly TestStressMetrics _metrics = metrics;
        private readonly int _renderDelayMilliseconds = renderDelayMilliseconds;

        public void Consume(ChainNode previous, ChainNode latest, long frameStartNanoseconds, CancellationToken cancellationToken)
        {
            _metrics.RecordFrame(previous, latest);
            if (_renderDelayMilliseconds > 0)
                Thread.Sleep(_renderDelayMilliseconds);
        }
    }

    private sealed class TestSlowDeferredSaveConsumer(TestSaveMetrics metrics) : IDeferredConsumer<WorldImage>
    {
        private readonly TestSaveMetrics _metrics = metrics;

        public long InitialDelayNanoseconds => 0L;

        public long ErrorRetryDelayNanoseconds => 1_000_000_000L;

        public Task<long> ConsumeAsync(WorldImage payload, CancellationToken cancellationToken) =>
            Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(5);
                    _metrics.IncrementSavesCompleted();
                }
                catch
                {
                    _metrics.IncrementSavesFailed();
                    throw;
                }

                var ticks = Stopwatch.GetTimestamp();
                var seconds = ticks / Stopwatch.Frequency;
                var remainder = ticks % Stopwatch.Frequency;
                var now = (seconds * 1_000_000_000L) + (remainder * 1_000_000_000L / Stopwatch.Frequency);
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

        public void RecordFrame(ChainNode? previous, ChainNode latest)
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
        private int _savesCompleted;
        private int _savesFailed;

        public int SavesCompleted => Volatile.Read(ref _savesCompleted);
        public int SavesFailed => Volatile.Read(ref _savesFailed);

        public void IncrementSavesCompleted() => Interlocked.Increment(ref _savesCompleted);

        public void IncrementSavesFailed() => Interlocked.Increment(ref _savesFailed);
    }
}
