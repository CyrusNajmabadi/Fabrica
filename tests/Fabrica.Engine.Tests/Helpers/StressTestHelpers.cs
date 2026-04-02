using System.Diagnostics;
using Fabrica.Core.Memory;
using Fabrica.Core.Threading;
using Fabrica.Engine.Simulation;
using Fabrica.Engine.World;
using Fabrica.Pipeline;
using Xunit;

namespace Fabrica.Engine.Tests.Helpers;

using ChainNode = BaseProductionLoop<WorldImage>.ChainNode;
using ChainNodeAllocator = BaseProductionLoop<WorldImage>.ChainNode.Allocator;

internal static class StressTestHelpers
{
    public static void RunEngine(
        StressTestMetrics metrics,
        int workerCount,
        CancellationToken cancellationToken,
        int renderDelayMilliseconds)
    {
        var nodePool = new ObjectPool<ChainNode, ChainNodeAllocator>(SimulationConstants.SnapshotPoolSize);
        var imagePool = new ObjectPool<WorldImage, WorldImage.Allocator>(SimulationConstants.SnapshotPoolSize);
        var shared = new SharedPipelineState<WorldImage>();
        var producer = new SimulationProducer(imagePool, workerCount);
        var consumer = new InvariantCheckingConsumer(metrics, renderDelayMilliseconds);
        var clock = new StressClock();

        var productionLoop = new ProductionLoop<WorldImage, SimulationProducer, StressClock, ThreadWaiter>(
            nodePool, shared, producer, clock, new ThreadWaiter(), TestPipelineConfiguration.Default);
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
        var nodePool = new ObjectPool<ChainNode, ChainNodeAllocator>(SimulationConstants.SnapshotPoolSize);
        var imagePool = new ObjectPool<WorldImage, WorldImage.Allocator>(SimulationConstants.SnapshotPoolSize);
        var shared = new SharedPipelineState<WorldImage>();
        var producer = new SimulationProducer(imagePool, workerCount);
        var consumer = new InvariantCheckingConsumer(metrics, renderDelayMilliseconds: 0);
        var clock = new StressClock();

        var saveConsumer = new SlowDeferredSaveConsumer(saveMetrics);
        var deferredConsumers = new IDeferredConsumer<WorldImage>[] { saveConsumer };

        var productionLoop = new ProductionLoop<WorldImage, SimulationProducer, StressClock, ThreadWaiter>(
            nodePool, shared, producer, clock, new ThreadWaiter(), TestPipelineConfiguration.Default);
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

        public void Consume(ChainNode previous, ChainNode latest, long frameStartNanoseconds, CancellationToken cancellationToken)
        {
            _metrics.RecordFrame(previous, latest);
            if (_renderDelayMilliseconds > 0)
                Thread.Sleep(_renderDelayMilliseconds);
        }
    }

    private sealed class SlowDeferredSaveConsumer(SaveMetrics metrics) : IDeferredConsumer<WorldImage>
    {
        private readonly SaveMetrics _metrics = metrics;

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
}

internal sealed class StressTestMetrics
{
    private long _framesRendered;
    private int _maxTickObserved;
    private int _lastLatestTick = -1;
    private volatile string? _invariantViolation;

    public long FramesRendered => Volatile.Read(ref _framesRendered);
    public int MaxTickObserved => Volatile.Read(ref _maxTickObserved);
    public string? InvariantViolation => _invariantViolation;

    public void RecordFrame(BaseProductionLoop<WorldImage>.ChainNode? previous, BaseProductionLoop<WorldImage>.ChainNode latest)
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

internal sealed class SaveMetrics
{
    private int _savesCompleted;
    private int _savesFailed;

    public int SavesCompleted => Volatile.Read(ref _savesCompleted);
    public int SavesFailed => Volatile.Read(ref _savesFailed);

    public void IncrementSavesCompleted() => Interlocked.Increment(ref _savesCompleted);

    public void IncrementSavesFailed() => Interlocked.Increment(ref _savesFailed);
}
