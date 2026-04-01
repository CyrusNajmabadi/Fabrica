using Engine.Memory;
using Engine.Threading;

namespace Engine.Pipeline;

/// <summary>
/// The production ("producer") thread.  Advances the chain one node at a time,
/// delegates domain-specific payload creation to a <typeparamref name="TProducer"/>,
/// and reclaims nodes the consumption thread has finished with.
///
/// Inherits chain management (node allocation, linking, cleanup, ref-counting)
/// from <see cref="BaseProductionLoop{TPayload}"/>.  This class adds the tick
/// loop, backpressure, and domain-specific producer/consumer coordination.
///
/// Generic on all type parameters (all constrained to struct) so the JIT/AOT
/// devirtualises all calls — zero interface-dispatch overhead in the hot path.
/// </summary>
internal sealed partial class ProductionLoop<TPayload, TProducer, TClock, TWaiter>(
    ObjectPool<BaseProductionLoop<TPayload>.ChainNode, BaseProductionLoop<TPayload>.ChainNode.Allocator> nodePool,
    SharedPipelineState<TPayload> shared,
    TProducer producer,
    TClock clock,
    TWaiter waiter)
    : BaseProductionLoop<TPayload>(nodePool, shared.PinnedVersions)
    where TProducer : struct, IProducer<TPayload>
    where TClock : struct, IClock
    where TWaiter : struct, IWaiter
{
    private readonly SharedPipelineState<TPayload> _shared = shared;
    private readonly TClock _clock = clock;
    private readonly TWaiter _waiter = waiter;
#pragma warning disable IDE0044 // Mutable struct — readonly would cause defensive copies
    private TProducer _producer = producer;
#pragma warning restore IDE0044

    public void Shutdown() => _producer.Shutdown();

    protected override void ReleasePayloadResources(TPayload payload) =>
        _producer.ReleaseResources(payload);

    public void Run(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        this.Bootstrap(cancellationToken);

        var lastTime = _clock.NowNanoseconds;
        long accumulator = 0;

        while (!cancellationToken.IsCancellationRequested)
            this.RunOneIteration(cancellationToken, ref lastTime, ref accumulator);
    }

    private void RunOneIteration(
        CancellationToken cancellationToken,
        ref long lastTime,
        ref long accumulator)
    {
        var now = _clock.NowNanoseconds;
        var delta = Math.Max(0, now - lastTime);
        lastTime = now;
        accumulator += delta;

        this.ProcessAvailableTicks(cancellationToken, ref accumulator);

        _waiter.Wait(new TimeSpan(SimulationConstants.IdleYieldNanoseconds / 100), cancellationToken);
    }

    private void ProcessAvailableTicks(CancellationToken cancellationToken, ref long accumulator)
    {
        while (accumulator >= SimulationConstants.TickDurationNanoseconds)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            this.ApplyPressureDelay(cancellationToken);
            this.Tick(cancellationToken);
            this.CleanupStaleNodes(_shared.ConsumptionEpoch);
            accumulator -= SimulationConstants.TickDurationNanoseconds;
        }
    }

    // ── Initialisation ───────────────────────────────────────────────────────

    private void Bootstrap(CancellationToken cancellationToken)
    {
        var payload = _producer.CreateInitialPayload(cancellationToken);
        var node = this.BootstrapChain(payload, _clock.NowNanoseconds);
        _shared.LatestNode = node;
    }

    // ── Tick ─────────────────────────────────────────────────────────────────

    private void Tick(CancellationToken cancellationToken)
    {
        var payload = _producer.Produce(this.CurrentNode!.Payload, cancellationToken);
        var node = this.AppendToChain(payload, _clock.NowNanoseconds);
        _shared.LatestNode = node;
    }

    // ── Backpressure ─────────────────────────────────────────────────────────

    private void ApplyPressureDelay(CancellationToken cancellationToken)
    {
        var gapNanoseconds = (this.CurrentSequence - _shared.ConsumptionEpoch)
                            * SimulationConstants.TickDurationNanoseconds;

        while (gapNanoseconds >= SimulationConstants.PressureHardCeilingNanoseconds)
        {
            _waiter.Wait(
                new TimeSpan(SimulationConstants.PressureMaxDelayNanoseconds / 100),
                cancellationToken);
            gapNanoseconds = (this.CurrentSequence - _shared.ConsumptionEpoch)
                           * SimulationConstants.TickDurationNanoseconds;
        }

        var delay = SimulationPressure.ComputeDelay(
            gapNanoseconds,
            SimulationConstants.PressureLowWaterMarkNanoseconds,
            SimulationConstants.TickDurationNanoseconds,
            SimulationConstants.PressureBucketCount,
            SimulationConstants.PressureBaseDelayNanoseconds,
            SimulationConstants.PressureMaxDelayNanoseconds);

        if (delay > 0)
            _waiter.Wait(new TimeSpan(delay / 100), cancellationToken);
    }
}
