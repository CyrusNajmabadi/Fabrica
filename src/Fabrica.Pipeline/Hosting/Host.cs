using Fabrica.Core.Threading;

namespace Fabrica.Pipeline.Hosting;

/// <summary>
/// Top-level coordinator: owns the production and consumption loops and manages their thread lifetimes.
///
/// ═══════════════════════ TWO-THREAD DESIGN OVERVIEW ═════════════════════════
///
///  PRODUCTION THREAD  (producer / memory owner)                   40 ticks/sec
///  ──────────────────────────────────────────────────────────────────────────
///  Bootstrap() → allocates sequence-0 node and publishes it
///  Tick loop:
///    • Delegate payload production to TProducer
///    • Append new ChainNode onto the forward chain:
///        [seq 0] → [seq 1] → [seq 2] → … → [seq N]
///         _oldest                            _current / LatestNode
///    • Volatile-write LatestNode = [seq N]  (release fence)
///    • CleanupStaleNodes(): walk chain from _oldest, freeing every
///      node whose sequence &lt; ConsumptionEpoch and is not pinned
///    • ApplyPressureDelay(): slow down if running too far ahead
///
///  CONSUMPTION THREAD  (consumer / deferred consumer coordinator)   ≈60 fps
///  ──────────────────────────────────────────────────────────────────────────
///  Frame loop:
///    • DrainCompletedDeferredTasks(): unpin nodes from finished async work
///    • Volatile-read LatestNode                     (acquire fence)
///    • MaybeRunDeferredConsumers(): check min-heap, dispatch due consumers
///    • Consume(previous, latest): called once both are non-null and distinct
///    • Volatile-write ConsumptionEpoch = sequence   (release fence)
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
///  SharedPipelineState.LatestNode        (volatile ChainNode&lt;TPayload&gt;?)
///    Written by production only; read by consumption.
///    The volatile release/acquire pair guarantees that all payload writes
///    made before the publish are visible to any thread that reads the node.
///    No additional synchronisation is needed to access payload fields.
///
///  SharedPipelineState.ConsumptionEpoch  (volatile int)
///    Written by consumption only; read by production.
///    Production frees sequence &lt; epoch.  Conservative race: if production
///    reads a stale (lower) epoch it retains a node one extra cleanup pass —
///    it never frees something the consumption thread is still touching.
///
///  SharedPipelineState.PinnedVersions    (ConcurrentDictionary-backed)
///    Thread-safe registry of sequences that deferred consumers hold.
///    Consumption thread pins before dispatching; threadpool tasks unpin on
///    completion; production thread reads IsPinned during cleanup.
///
/// ═══════════════════════ WHY NO LOCKS IN THE HOT PATH ══════════════════════
///
///  LatestNode and ConsumptionEpoch each have at most one writer thread.
///  Volatile fences provide the required visibility across CPUs.
///  The epoch is conservative, so races can only make the system hold memory
///  slightly longer — they can never corrupt state or free a live object.
///
///  PinnedVersions uses ConcurrentDictionary because Unpin arrives from a
///  threadpool task.  Pinning only happens at deferred-consumer boundaries
///  (infrequent), so it is not on the hot path.  See PinnedVersions for details.
///
/// ═══════════════════════ PARALLELISM OPPORTUNITIES ═════════════════════════
///
///  MULTITHREADED PRODUCTION (future)
///    _currentNode is never freed by cleanup (it is explicitly excluded by
///    the _oldestNode != _currentNode guard).  Its payload is fully immutable
///    once published.  The producer can spawn any number of worker threads to
///    read the current payload and compute parts of the next state in parallel
///    — all workers read immutable data, and their output goes into a fresh
///    payload not yet visible to any other thread.  No locks or atomics are
///    required between workers and the owning thread beyond a final join/await
///    before publishing the new node.
///
///  MULTITHREADED CONSUMPTION
///    When the consumer dispatches to parallel workers, both 'previous' and
///    'latest' nodes — and the entire chain between them — are guaranteed alive:
///      • 'latest' is at sequence M; ConsumptionEpoch has not advanced past M.
///      • 'previous' is at sequence N ≤ M; epoch = N, and cleanup frees only
///        sequence &lt; N (strictly less than), so sequence N itself is never freed.
///    All payloads are fully immutable.  Workers can safely read any node in
///    the chain without synchronization.
///    Constraint: all workers must finish before the consumer call returns,
///    because ConsumptionEpoch advances immediately afterward and the
///    production loop may then reclaim 'previous' on the very next cleanup.
///
///  TEMPORAL DECOUPLING
///    Consumption always operates on already-produced, immutable nodes.
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
///  CONSUMPTION → PRODUCTION BACK-PRESSURE (epoch gap)
///    Because consumption always reads LatestNode (the most recent tick),
///    a slow consumption frame rate does not by itself cause pressure:
///    each frame still advances the epoch to the latest tick and allows
///    cleanup to reclaim the entire backlog in one pass.
///
///    Pressure builds only when the consumption thread stalls long enough
///    for production to run far ahead.  The epoch gap (produced sequences
///    minus consumption epoch) is the sole pressure signal.  With a 40 Hz
///    tick rate:
///      • Each 1/8 of the hard-ceiling gap adds an exponential pre-tick
///        delay (1 ms → 2 ms → 4 ms → … → 64 ms).
///      • At the hard ceiling the production loop busy-waits until the
///        consumption thread advances the epoch, preventing unbounded
///        growth of the chain.
///    Note: ObjectPool.Rent() never blocks — it allocates when empty.
///    Pressure is therefore based on the epoch gap, not pool occupancy.
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

            // Capturing lambda — allocated once per thread, not on a hot path.
            var thread = new Thread(() =>
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
                    // Signal the other loop to exit so it doesn't continue running after this one has faulted.
                    linkedCancellationTokenSource.Cancel();
                    completion.SetException(ex);
                }
            })
            {
                Name = name,
                IsBackground = false,
            };

            thread.Start();
            return completion.Task;
        }
    }
}
