using Fabrica.Core.Collections;
using Fabrica.Core.Memory;
using Fabrica.Core.Threading;
using Fabrica.Engine.Rendering;
using Fabrica.Engine.Simulation;
using Fabrica.Engine.World;
using Fabrica.Pipeline;
using Fabrica.Pipeline.Hosting;

namespace Fabrica.Engine.Hosting;

/// <summary>
/// Factory for the default simulation engine configuration.
/// </summary>
internal static class SimulationEngine
{
    public static Host<WorldImage, SimulationProducer, RenderConsumer<TRenderer>, TClock, TWaiter>
        Create<TRenderer, TClock, TWaiter>(
            TClock clock,
            TWaiter waiter,
            TRenderer renderer,
            int simulationWorkerCount,
            int renderWorkerCount,
            params IDeferredConsumer<WorldImage>[] deferredConsumers)
        where TRenderer : struct, IRenderer
        where TClock : struct, IClock
        where TWaiter : struct, IWaiter
    {
        var config = new PipelineConfiguration
        {
            TickDurationNanoseconds = SimulationConstants.TickDurationNanoseconds,
            IdleYieldNanoseconds = SimulationConstants.IdleYieldNanoseconds,
            PressureLowWaterMarkNanoseconds = SimulationConstants.PressureLowWaterMarkNanoseconds,
            PressureHardCeilingNanoseconds = SimulationConstants.PressureHardCeilingNanoseconds,
            PressureBucketCount = SimulationConstants.PressureBucketCount,
            PressureBaseDelayNanoseconds = SimulationConstants.PressureBaseDelayNanoseconds,
            PressureMaxDelayNanoseconds = SimulationConstants.PressureMaxDelayNanoseconds,
            RenderIntervalNanoseconds = SimulationConstants.RenderIntervalNanoseconds,
        };

        var queue = new ProducerConsumerQueue<PipelineEntry<WorldImage>>();
        var imagePool = new ObjectPool<WorldImage, WorldImage.Allocator>(SimulationConstants.SnapshotPoolSize);
        var shared = new SharedPipelineState<WorldImage>(queue);

        var producer = new SimulationProducer(imagePool, simulationWorkerCount);
        var consumer = new RenderConsumer<TRenderer>(renderWorkerCount, renderer);

        return new Host<WorldImage, SimulationProducer, RenderConsumer<TRenderer>, TClock, TWaiter>(
            new ProductionLoop<WorldImage, SimulationProducer, TClock, TWaiter>(
                shared, producer, clock, waiter, config),
            new ConsumptionLoop<WorldImage, RenderConsumer<TRenderer>, TClock, TWaiter>(
                shared, consumer, clock, waiter, deferredConsumers, config));
    }
}
