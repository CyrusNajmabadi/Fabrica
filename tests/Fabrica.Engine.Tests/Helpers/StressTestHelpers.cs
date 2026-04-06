using System.Diagnostics;
using Fabrica.Core.Collections;
using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.Core.Threading;
using Fabrica.Engine.Simulation;
using Fabrica.Engine.World;
using Fabrica.Pipeline;
using Xunit;

namespace Fabrica.Engine.Tests.Helpers;

internal static class StressTestHelpers
{
    public static void RunEngine(
        StressTestMetrics metrics,
        int workerCount,
        CancellationToken cancellationToken,
        int renderDelayMilliseconds)
    {
        using var workerPool = new WorkerPool(workerCount);
        var scheduler = new JobScheduler(workerPool);

        var queue = new ProducerConsumerQueue<PipelineEntry<WorldImage>>();
        var imagePool = new ObjectPool<WorldImage, WorldImage.Allocator>(SimulationConstants.SnapshotPoolSize);
        var shared = new SharedPipelineState<WorldImage>(queue);
        var producer = new SimulationProducer(imagePool, scheduler);
        var consumer = new InvariantCheckingConsumer(metrics, renderDelayMilliseconds);
        var clock = new StressClock();

        var productionLoop = new ProductionLoop<WorldImage, SimulationProducer, StressClock, ThreadWaiter>(
            shared, producer, clock, new ThreadWaiter(), TestPipelineConfiguration.Default);
        var consumptionLoop = new ConsumptionLoop<WorldImage, InvariantCheckingConsumer, StressClock, ThreadWaiter>(
            shared, consumer, clock, new ThreadWaiter(), [], TestPipelineConfiguration.Default);

        RunBothLoops(productionLoop, consumptionLoop, cancellationToken, metrics);
    }

    public static void RunEngineWithDeferredSave(
        StressTestMetrics metrics,
        SaveMetrics saveMetrics,
        int workerCount,
        CancellationToken cancellationToken)
    {
        using var workerPool = new WorkerPool(workerCount);
        var scheduler = new JobScheduler(workerPool);

        var queue = new ProducerConsumerQueue<PipelineEntry<WorldImage>>();
        var imagePool = new ObjectPool<WorldImage, WorldImage.Allocator>(SimulationConstants.SnapshotPoolSize);
        var shared = new SharedPipelineState<WorldImage>(queue);
        var producer = new SimulationProducer(imagePool, scheduler);
        var consumer = new InvariantCheckingConsumer(metrics, renderDelayMilliseconds: 0);
        var clock = new StressClock();

        var saveConsumer = new SlowDeferredSaveConsumer(saveMetrics);
        var deferredConsumers = new IDeferredConsumer<WorldImage>[] { saveConsumer };

        var productionLoop = new ProductionLoop<WorldImage, SimulationProducer, StressClock, ThreadWaiter>(
            shared, producer, clock, new ThreadWaiter(), TestPipelineConfiguration.Default);
        var consumptionLoop = new ConsumptionLoop<WorldImage, InvariantCheckingConsumer, StressClock, ThreadWaiter>(
            shared, consumer, clock, new ThreadWaiter(), deferredConsumers, TestPipelineConfiguration.Default);

        RunBothLoops(productionLoop, consumptionLoop, cancellationToken, metrics);
    }

    private static void RunBothLoops<TConsumer>(
        ProductionLoop<WorldImage, SimulationProducer, StressClock, ThreadWaiter> productionLoop,
        ConsumptionLoop<WorldImage, TConsumer, StressClock, ThreadWaiter> consumptionLoop,
        CancellationToken cancellationToken,
        StressTestMetrics metrics)
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

    internal readonly struct StressClock : IClock
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

    internal readonly struct InvariantCheckingConsumer(StressTestMetrics metrics, int renderDelayMilliseconds) : IConsumer<WorldImage>
    {
        private readonly StressTestMetrics _metrics = metrics;
        private readonly int _renderDelayMilliseconds = renderDelayMilliseconds;

        public void Consume(
            in ProducerConsumerQueue<PipelineEntry<WorldImage>>.Segment entries,
            long frameStartNanoseconds,
            CancellationToken cancellationToken)
        {
            var previous = entries[0];
            var latest = entries[^1];
            _metrics.RecordFrame(in previous, in latest);
            if (_renderDelayMilliseconds > 0)
                Thread.Sleep(_renderDelayMilliseconds);
        }
    }

    private sealed class SlowDeferredSaveConsumer(SaveMetrics metrics) : IDeferredConsumer<WorldImage>
    {
        private readonly SaveMetrics _metrics = metrics;

        public long InitialDelayNanoseconds => 0L;

        public long ErrorRetryDelayNanoseconds => 1_000_000_000L;

        public Task<long> ConsumeAsync(WorldImage payload, CancellationToken cancellationToken)
            => Task.Run(() =>
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
}

internal sealed class StressTestMetrics
{
    private long _framesRendered;
    private long _lastLatestPublishTime;
    private long _lastLatestTick = -1;
    private volatile string? _invariantViolation;

    public long FramesRendered => Volatile.Read(ref _framesRendered);
    public string? InvariantViolation => _invariantViolation;

    public void RecordFrame(in PipelineEntry<WorldImage> previous, in PipelineEntry<WorldImage> latest)
    {
        Interlocked.Increment(ref _framesRendered);

        if (previous.Tick >= latest.Tick)
        {
            _invariantViolation ??=
                $"Previous tick ({previous.Tick}) >= latest tick ({latest.Tick})";
            return;
        }

        var previousLastTick = Volatile.Read(ref _lastLatestTick);
        if (previousLastTick >= 0 && latest.Tick <= previousLastTick)
        {
            _invariantViolation ??=
                $"Latest tick went backwards: {latest.Tick} <= {previousLastTick}";
            return;
        }
        Volatile.Write(ref _lastLatestTick, latest.Tick);

        var latestPublishTime = latest.PublishTimeNanoseconds;
        var previousLatestPublishTime = Volatile.Read(ref _lastLatestPublishTime);
        if (latestPublishTime < previousLatestPublishTime)
        {
            _invariantViolation ??=
                $"Latest publish time went backwards: {latestPublishTime} < {previousLatestPublishTime}";
            return;
        }
        Volatile.Write(ref _lastLatestPublishTime, latestPublishTime);

        if (previous.PublishTimeNanoseconds > latestPublishTime)
        {
            _invariantViolation ??=
                $"Previous publish time ({previous.PublishTimeNanoseconds}) > latest ({latestPublishTime})";
            return;
        }
    }
}

internal sealed class SaveMetrics
{
    private int _savesCompleted;
    private int _savesFailed;

    public int SavesCompleted => Volatile.Read(ref _savesCompleted);
    public int SavesFailed => Volatile.Read(ref _savesFailed);

    public void IncrementSavesCompleted()
        => Interlocked.Increment(ref _savesCompleted);

    public void IncrementSavesFailed()
        => Interlocked.Increment(ref _savesFailed);
}
