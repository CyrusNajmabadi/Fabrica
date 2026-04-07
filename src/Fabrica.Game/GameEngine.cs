using Fabrica.Core.Jobs;
using Fabrica.Core.Threading;
using Fabrica.Core.Threading.Queues;
using Fabrica.Pipeline;
using Fabrica.Pipeline.Hosting;

namespace Fabrica.Game;

/// <summary>
/// Factory for the game pipeline. Creates all shared infrastructure and returns a fully
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
        var shared = new SharedPipelineState<GameWorldImage>(queue);

        // WorkerPool, JobScheduler, and GameTickState are created here (not inside the producer)
        // because the future consumer/renderer will share them: the consumer reads node data from
        // the stores to render snapshots, and ReleaseResources (called when the consumer retires
        // a snapshot) decrements root refcounts on those same stores. The worker pool may also be
        // shared for read-only render jobs in the future.
        var workerPool = new WorkerPool(simulationWorkerCount);
        var scheduler = new JobScheduler(workerPool);
        var tickState = new GameTickState(workerPool.WorkerCount);

        var producer = new GameProducer(scheduler, tickState);

        return new Host<GameWorldImage, GameProducer, TConsumer, TClock, TWaiter>(
            new ProductionLoop<GameWorldImage, GameProducer, TClock, TWaiter>(
                shared, producer, clock, waiter, config),
            new ConsumptionLoop<GameWorldImage, TConsumer, TClock, TWaiter>(
                shared, consumer, clock, waiter, deferredConsumers, config));
    }
}
