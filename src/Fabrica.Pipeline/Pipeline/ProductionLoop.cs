using System.Diagnostics;
using Fabrica.Core.Collections;
using Fabrica.Core.Threading;

namespace Fabrica.Pipeline;

/// <summary>
/// The production ("producer") thread. Appends pipeline entries to the <see cref="ProducerConsumerQueue{T}"/>, delegates
/// domain-specific payload creation to a <typeparamref name="TProducer"/>, cleans up entries the consumption thread has advanced
/// past, and manages deferred-release of pinned payloads.
///
/// QUEUE-BASED DESIGN
///   Replaces the previous ChainNode linked-list model. Each tick appends a <see cref="PipelineEntry{TPayload}"/> to the shared
///   queue. The queue's volatile producer/consumer positions provide all SPSC synchronization — no separate LatestNode or
///   ConsumptionEpoch fields are needed.
///
/// CLEANUP &amp; PINNING
///   <see cref="ProducerConsumerQueue{T}.ProducerCleanup{THandler}"/> walks entries the consumer has advanced past. For each
///   entry, the <see cref="CleanupHandler"/> checks <see cref="PinnedVersions.IsPinned"/>:
///     • Not pinned → release payload resources immediately via <typeparamref name="TProducer"/>.
///     • Pinned → stash the payload in <c>_pinnedPayloads</c>. A subsequent <see cref="DrainUnpinnedPayloads"/> pass releases
///       them once the deferred consumer has finished and the pin has been removed.
///
/// Generic on all type parameters (all constrained to struct) so the JIT/AOT devirtualises all calls — zero interface-dispatch
/// overhead in the hot path.
/// </summary>
public sealed partial class ProductionLoop<TPayload, TProducer, TClock, TWaiter>(
    SharedPipelineState<TPayload> shared,
    TProducer producer,
    TClock clock,
    TWaiter waiter,
    PipelineConfiguration config)
    where TProducer : struct, IProducer<TPayload>
    where TClock : struct, IClock
    where TWaiter : struct, IWaiter
{
    private readonly PipelineConfiguration _config = config;
    private readonly SharedPipelineState<TPayload> _shared = shared;
    private readonly TClock _clock = clock;
    private readonly TWaiter _waiter = waiter;
#pragma warning disable IDE0044 // Mutable struct — readonly would cause defensive copies
    private TProducer _producer = producer;
#pragma warning restore IDE0044

    /// <summary>Most recently produced payload. Kept so the next tick can derive the new payload from it.</summary>
    private TPayload _currentPayload = default!;

    /// <summary>Next tick number to assign. Incremented after each append.</summary>
    private long _nextTick;

    /// <summary>Payloads whose queue positions were pinned at cleanup time. Keyed by queue position so we can match the unpin
    /// callback. Released when <see cref="DrainUnpinnedPayloads"/> finds the pin has been removed.</summary>
    private readonly Dictionary<long, TPayload> _pinnedPayloads = [];

    /// <summary>Reusable buffer to avoid allocating during <see cref="DrainUnpinnedPayloads"/>.</summary>
    private readonly List<long> _drainBuffer = [];

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

        _waiter.Wait(new TimeSpan(_config.IdleYieldNanoseconds / 100), cancellationToken);
    }

    private void ProcessAvailableTicks(CancellationToken cancellationToken, ref long accumulator)
    {
        while (accumulator >= _config.TickDurationNanoseconds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            this.ApplyPressureDelay(cancellationToken);
            this.Tick(cancellationToken);
            this.Cleanup();
            accumulator -= _config.TickDurationNanoseconds;
        }
    }

    // ── Initialisation ───────────────────────────────────────────────────────

    private void Bootstrap(CancellationToken cancellationToken)
    {
        _currentPayload = _producer.CreateInitialPayload(cancellationToken);
        this.AppendEntry(_currentPayload);
    }

    // ── Tick ─────────────────────────────────────────────────────────────────

    private void Tick(CancellationToken cancellationToken)
    {
        _currentPayload = _producer.Produce(_currentPayload, cancellationToken);
        this.AppendEntry(_currentPayload);
    }

    private void AppendEntry(TPayload payload)
    {
        var tick = _nextTick++;
        _shared.Queue.ProducerAppend(new PipelineEntry<TPayload>
        {
            Payload = payload,
            Tick = tick,
            PublishTimeNanoseconds = _clock.NowNanoseconds,
        });
        Debug.Assert(_nextTick == _shared.Queue.ProducerPosition, "Tick counter and queue position must advance in lockstep.");
    }

    // ── Cleanup ──────────────────────────────────────────────────────────────

    private void Cleanup()
    {
        var handler = new CleanupHandler(this);
        _shared.Queue.ProducerCleanup(ref handler);
        this.DrainUnpinnedPayloads();
    }

    private void DrainUnpinnedPayloads()
    {
        if (_pinnedPayloads.Count == 0)
            return;

        foreach (var (position, _) in _pinnedPayloads)
        {
            if (!_shared.PinnedVersions.IsPinned(position))
                _drainBuffer.Add(position);
        }

        foreach (var position in _drainBuffer)
        {
            _producer.ReleaseResources(_pinnedPayloads[position]);
            _pinnedPayloads.Remove(position);
        }

        _drainBuffer.Clear();
    }

    /// <summary>
    /// Cleanup handler for <see cref="ProducerConsumerQueue{T}.ProducerCleanup{THandler}"/>. Checks pinning status and either
    /// releases resources immediately or stashes the payload for deferred release.
    /// </summary>
    private readonly struct CleanupHandler(ProductionLoop<TPayload, TProducer, TClock, TWaiter> loop)
        : ProducerConsumerQueue<PipelineEntry<TPayload>>.ICleanupHandler
    {
        public void HandleCleanup(long position, in PipelineEntry<TPayload> item)
        {
            if (loop._shared.PinnedVersions.IsPinned(position))
                loop._pinnedPayloads[position] = item.Payload;
            else
                loop._producer.ReleaseResources(item.Payload);
        }
    }

    // ── Backpressure ─────────────────────────────────────────────────────────

    private void ApplyPressureDelay(CancellationToken cancellationToken)
    {
        var gapNanoseconds = (_shared.Queue.ProducerPosition - _shared.Queue.ConsumerPosition)
                            * _config.TickDurationNanoseconds;

        while (gapNanoseconds >= _config.PressureHardCeilingNanoseconds)
        {
            _waiter.Wait(
                new TimeSpan(_config.PressureMaxDelayNanoseconds / 100),
                cancellationToken);
            gapNanoseconds = (_shared.Queue.ProducerPosition - _shared.Queue.ConsumerPosition)
                           * _config.TickDurationNanoseconds;
        }

        var delay = SimulationPressure.ComputeDelay(
            gapNanoseconds,
            _config.PressureLowWaterMarkNanoseconds,
            _config.TickDurationNanoseconds,
            _config.PressureBucketCount,
            _config.PressureBaseDelayNanoseconds,
            _config.PressureMaxDelayNanoseconds);

        if (delay > 0)
            _waiter.Wait(new TimeSpan(delay / 100), cancellationToken);
    }
}
