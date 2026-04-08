using Fabrica.Core.Threading;

namespace Fabrica.Pipeline.Hosting;

/// <summary>
/// Top-level coordinator: owns the production and consumption loops and manages their thread lifetimes.
///
/// ═══════════════════════ TWO-THREAD DESIGN OVERVIEW ═════════════════════════
///
///  PRODUCTION THREAD  (producer / memory owner)                   40 ticks/sec
///  ──────────────────────────────────────────────────────────────────────────
///  Bootstrap() → appends the initial entry to the queue
///  Tick loop:
///    • Delegate payload production to TProducer
///    • Append new PipelineEntry to the queue (volatile write of producer position)
///    • Cleanup: walk entries the consumer has advanced past, releasing resources
///      (checking PinnedVersions for deferred holds)
///    • ApplyPressureDelay(): slow down if running too far ahead
///
///  CONSUMPTION THREAD  (consumer / deferred consumer coordinator)   ≈60 fps
///  ──────────────────────────────────────────────────────────────────────────
///  Frame loop:
///    • DrainCompletedDeferredTasks(): release pins from finished async work
///    • ConsumerAcquire(): volatile-read producer position (acquire fence)
///    • MaybeRunDeferredConsumers(): check min-heap, dispatch due consumers
///    • Consume(entries): called when segment has ≥ 2 entries (previous + new)
///    • ConsumerAdvance(count - 1): hold back last entry for interpolation
///    • ThrottleToFrameRate()
///
///  DEFERRED CONSUMERS  (threadpool — dispatched by consumption thread)
///  ──────────────────────────────────────────────────────────────────────────
///    • Consumer.ConsumeAsync(payload, cancellationToken)
///    • Returns Task&lt;long&gt; with next-run wall-clock nanoseconds
///    • Loop auto-pins before dispatch, auto-unpins on completion
///
/// ══════════════════════════════ SHARED STATE ════════════════════════════════
///
///  All cross-thread communication lives in SharedPipelineState&lt;TPayload&gt;:
///
///  ProducerConsumerQueue (SPSC volatile positions)
///    The queue's producer position (volatile write by producer, volatile read
///    by consumer) replaces the old LatestNode field. Its consumer position
///    (volatile write by consumer, volatile read by producer) replaces the old
///    ConsumptionEpoch. The release/acquire fences ensure all entry data is
///    visible across threads without additional synchronisation.
///
///  PinnedVersions (ConcurrentDictionary-backed)
///    Thread-safe registry of queue positions that deferred consumers hold.
///    Consumption thread pins before dispatching; threadpool tasks unpin on
///    completion; production thread reads IsPinned during cleanup.
///
/// ═══════════════════════ WHY NO LOCKS IN THE HOT PATH ══════════════════════
///
///  The queue's producer and consumer positions each have at most one writer.
///  Volatile fences provide the required visibility across CPUs. Staleness in
///  either direction is conservative: a stale producer position means the
///  consumer sees fewer entries (processes them next frame); a stale consumer
///  position means the producer retains entries longer (delays cleanup, never
///  frees prematurely).
///
///  PinnedVersions uses ConcurrentDictionary because Unpin arrives from a
///  threadpool task.  Pinning only happens at deferred-consumer boundaries
///  (infrequent), so it is not on the hot path.  See PinnedVersions for details.
///
/// ═══════════════════════ PARALLELISM OPPORTUNITIES ═════════════════════════
///
///  MULTITHREADED PRODUCTION (future)
///    The current payload is fully immutable once published. The producer can
///    spawn any number of worker threads to read the current payload and compute
///    parts of the next state in parallel — all workers read immutable data,
///    and their output goes into a fresh payload not yet visible to any other
///    thread. No locks or atomics are required between workers and the owning
///    thread beyond a final join/await before publishing the new entry.
///
///  MULTITHREADED CONSUMPTION
///    When the consumer dispatches to parallel workers, the entire segment from
///    the previous entry through the latest is guaranteed alive — none of those
///    positions have been advanced past yet. All payloads are fully immutable.
///    Workers can safely read any entry in the segment without synchronization.
///    Constraint: all workers must finish before the consumer call returns,
///    because ConsumerAdvance happens immediately afterward and the production
///    loop may then reclaim earlier entries on the very next cleanup.
///
///  TEMPORAL DECOUPLING
///    Consumption always operates on already-produced, immutable entries.
///    The production loop is concurrently producing ticks further ahead.
///    The two threads are temporally decoupled: consumption is always at a
///    point in the past relative to the latest produced state, and neither
///    thread blocks the other's internal parallelism.
///
/// ════════════════════════════════ THROTTLING ════════════════════════════════
///
///  PRODUCTION SELF-THROTTLING (wall time)
///    The production loop uses a fixed-timestep accumulator (see ProductionLoop).
///    If each tick is cheap, the loop yields for most of each 25 ms window.
///    If a tick takes longer than 25 ms of wall time, the accumulator falls
///    behind and game time runs slower than real time — the loop always runs
///    as fast as it can, never artificially fast and never artificially
///    throttled just to hit a fixed Hz when it cannot sustain it.
///
///  CONSUMPTION → PRODUCTION BACK-PRESSURE (queue depth)
///    Because consumption acquires the latest entries each frame and advances
///    past all but one, a slow consumption frame rate does not by itself cause
///    pressure: each frame still catches up to the latest entry and releases
///    the backlog for cleanup.
///
///    Pressure builds only when the consumption thread stalls long enough
///    for production to run far ahead. The queue depth (producer position
///    minus consumer position) is the sole pressure signal. With a 40 Hz
///    tick rate:
///      • Each 1/8 of the hard-ceiling gap adds an exponential pre-tick
///        delay (1 ms → 2 ms → 4 ms → … → 64 ms).
///      • At the hard ceiling the production loop busy-waits until the
///        consumption thread advances, preventing unbounded queue growth.
///
/// ════════════════════════════════════════════════════════════════════════════
///
/// Use the explicit constructor to inject custom loops (e.g. in tests).
/// </summary>
public sealed class Host<TPayload, TProducer, TConsumer, TClock, TWaiter>(
    ProductionLoop<TPayload, TProducer, TClock, TWaiter> productionLoop,
    ConsumptionLoop<TPayload, TConsumer, TClock, TWaiter> consumptionLoop)
    where TProducer : struct, IProducer<TPayload>
    where TConsumer : struct, IConsumer<TPayload>
    where TClock : struct, IClock
    where TWaiter : struct, IWaiter
{
    private readonly ProductionLoop<TPayload, TProducer, TClock, TWaiter> _productionLoop = productionLoop;
    private readonly ConsumptionLoop<TPayload, TConsumer, TClock, TWaiter> _consumptionLoop = consumptionLoop;

    /// <summary>
    /// Starts both loops on dedicated threads and returns a task that completes when both exit. Each thread's outcome is tracked
    /// by a <see cref="TaskCompletionSource"/> so that <see cref="Task.WhenAll"/> provides automatic join and exception
    /// aggregation. A linked <see cref="CancellationTokenSource"/> ensures a fault in one loop cancels the other so it can exit
    /// promptly.
    ///
    /// Dedicated <see cref="Thread"/> objects are still used for execution (control over naming, <see cref="Thread.IsBackground"/>,
    /// and stack size), with <see cref="TaskCompletionSource"/> layered on top purely for lifecycle and error tracking.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var linkedCancellationToken = linkedCancellationTokenSource.Token;

        var productionTask = StartLoopTask(_productionLoop.Run, "Production");
        var consumptionTask = StartLoopTask(_consumptionLoop.Run, "Consumption");

        await Task.WhenAll(productionTask, consumptionTask).ConfigureAwait(false);
        return;

        Task StartLoopTask(Action<CancellationToken> loopAction, string name)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            ThreadPinningNative.StartNativeThreadWithHighQos(
                name,
                () =>
                {
                    try
                    {
                        loopAction(linkedCancellationToken);
                        completion.SetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        completion.SetCanceled(linkedCancellationToken);
                    }
                    catch (Exception ex)
                    {
                        linkedCancellationTokenSource.Cancel();
                        completion.SetException(ex);
                    }
                },
                isBackground: false);

            return completion.Task;
        }
    }
}
