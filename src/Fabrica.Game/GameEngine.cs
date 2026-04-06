using Fabrica.Core.Collections;
using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.Core.Threading;
using Fabrica.Pipeline;
using Fabrica.Pipeline.Hosting;

namespace Fabrica.Game;

/// <summary>
/// Factory for the game pipeline. Creates all stores, TLBs, worker threads, and returns a fully
/// wired <see cref="Host{TPayload,TProducer,TConsumer,TClock,TWaiter}"/> ready to run.
/// </summary>
public static class GameEngine
{
    public static Host<GameWorldImage, GameProducer, TConsumer, TClock, TWaiter>
        Create<TConsumer, TClock, TWaiter>(
            TClock clock,
            TWaiter waiter,
            TConsumer consumer,
            PipelineConfiguration config,
            int simulationWorkerCount = -1,
            params IDeferredConsumer<GameWorldImage>[] deferredConsumers)
        where TConsumer : struct, IConsumer<GameWorldImage>
        where TClock : struct, IClock
        where TWaiter : struct, IWaiter
    {
        var queue = new ProducerConsumerQueue<PipelineEntry<GameWorldImage>>();
        var imagePool = new ObjectPool<GameWorldImage, GameWorldImage.Allocator>(initialCapacity: 8);
        var shared = new SharedPipelineState<GameWorldImage>(queue);

        var workerPool = new WorkerPool(simulationWorkerCount);
        var scheduler = new JobScheduler(workerPool);
        var tickState = new GameTickState(workerPool.WorkerCount);

        var producer = new GameProducer(imagePool, scheduler, tickState);

        return new Host<GameWorldImage, GameProducer, TConsumer, TClock, TWaiter>(
            new ProductionLoop<GameWorldImage, GameProducer, TClock, TWaiter>(
                shared, producer, clock, waiter, config),
            new ConsumptionLoop<GameWorldImage, TConsumer, TClock, TWaiter>(
                shared, consumer, clock, waiter, deferredConsumers, config));
    }
}
