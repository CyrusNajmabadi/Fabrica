using Simulation.Memory;
using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Top-level coordinator: owns the production and consumption loops and manages
/// their thread lifetimes.
///
/// ═══════════════════════ TWO-THREAD DESIGN OVERVIEW ═════════════════════════
///
///  PRODUCTION THREAD  (producer / memory owner)                   40 ticks/sec
///  ──────────────────────────────────────────────────────────────────────────
///  Bootstrap() → allocates sequence-0 node and publishes it
///  Tick loop:
///    • Delegate payload production to TProducer
///    • Append new TNode onto the forward chain:
///        [seq 0] → [seq 1] → [seq 2] → … → [seq N]
///         _oldest                            _current / LatestNode
///    • Volatile-write LatestNode = [seq N]  (release fence)
///    • CleanupStaleNodes(): walk chain from _oldest, freeing every
///      node whose sequence &lt; ConsumptionEpoch and is not pinned
///    • ApplyPressureDelay(): slow down if running too far ahead
///
///  CONSUMPTION THREAD  (consumer / save coordinator)               ≈60 fps
///  ──────────────────────────────────────────────────────────────────────────
///  Frame loop:
///    • Volatile-read LatestNode                     (acquire fence)
///    • MaybeStartSave(): if a save is due, pin the node and hand it
///      off to the save task before advancing the epoch
///    • Consume(previous, latest): always called, even if node unchanged
///    • Volatile-write ConsumptionEpoch = sequence   (release fence)
///    • ThrottleToFrameRate()
///
///  SAVE TASK  (threadpool — spawned by consumption thread)
///  ──────────────────────────────────────────────────────────────────────────
///    • Saver.Save(node, sequence)
///    • PinnedVersions.Unpin(sequence)  ← releases the production hold
///    • NextSaveAtTick = sequence + SaveIntervalTicks
///
/// ══════════════════════════════ SHARED STATE ════════════════════════════════
///
///  Exactly three volatile fields cross the thread boundary:
///
///  SharedState.LatestNode        (volatile TNode?)
///    Written by production only; read by consumption.
///    The volatile release/acquire pair guarantees that all payload writes
///    made before the publish are visible to any thread that reads the node.
///    No additional synchronisation is needed to access payload fields.
///
///  SharedState.ConsumptionEpoch  (volatile int)
///    Written by consumption only; read by production.
///    Production frees sequence &lt; epoch.  Conservative race: if production
///    reads a stale (lower) epoch it retains a node one extra cleanup pass —
///    it never frees something the consumption thread is still touching.
///
///  SharedState.NextSaveAtTick   (volatile int)
///    Written by consumption (sets to 0) AND the save task (sets next interval).
///    The two writes do not conflict because consumption only writes 0 while
///    the field is non-zero, and the save task writes non-zero later from
///    a different execution context.
///
/// ═══════════════════════ WHY NO LOCKS IN THE HOT PATH ══════════════════════
///
///  Each field above has at most one writer thread in the hot path.
///  Volatile fences provide the required visibility across CPUs.
///  The epoch is conservative, so races can only make the system hold memory
///  slightly longer — they can never corrupt state or free a live object.
///
///  The sole exception is PinnedVersions (node pinning for saves), which
///  uses ConcurrentDictionary because Unpin arrives from a threadpool save
///  task.  Pinning only happens at save boundaries (≈every 5 minutes of game
///  time), so it is not on the hot path.  See PinnedVersions for details.
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
///    for production to produce more nodes than the pool can hold.  With
///    a 256-slot pool at 40 Hz, production can run unimpeded for ≈6.4 seconds
///    before the pool fills.  Beyond that:
///      • Each 1/8 capacity bucket filled adds an exponential pre-tick delay
///        (1 ms → 2 ms → 4 ms → … → 64 ms).
///      • Full pool exhaustion blocks Tick() entirely until a slot is freed.
///    This ensures production never silently allocates unboundedly and
///    instead applies explicit back-pressure that scales with the stall depth.
///
/// ════════════════════════════════════════════════════════════════════════════
///
/// Use the explicit constructor to inject custom loops (e.g. in tests).
/// For the default simulation configuration, see <see cref="SimulationEngine"/>.
/// </summary>
internal sealed class Engine<TNode, TProducer, TConsumer, TSaveRunner, TSaver, TClock, TWaiter>
    where TNode : ChainNode<TNode>, new()
    where TProducer : struct, IProducer<TNode>
    where TConsumer : struct, IConsumer<TNode>
    where TSaveRunner : struct, ISaveRunner<TNode>
    where TSaver : struct, ISaver<TNode>
    where TClock : struct, IClock
    where TWaiter : struct, IWaiter
{
    private readonly ProductionLoop<TNode, TProducer, TClock, TWaiter> _productionLoop;
    private readonly ConsumptionLoop<TNode, TConsumer, TSaveRunner, TSaver, TClock, TWaiter> _consumptionLoop;
    private readonly IDisposable[] _disposables;

    public Engine(
        ProductionLoop<TNode, TProducer, TClock, TWaiter> productionLoop,
        ConsumptionLoop<TNode, TConsumer, TSaveRunner, TSaver, TClock, TWaiter> consumptionLoop,
        params IDisposable[] disposables)
    {
        _productionLoop = productionLoop;
        _consumptionLoop = consumptionLoop;
        _disposables = disposables;
    }

    /// <summary>
    /// Starts both loops on dedicated threads and blocks until both exit.
    /// </summary>
    public void Run(CancellationToken cancellationToken)
    {
        try
        {
            var productionThread = new Thread(() => _productionLoop.Run(cancellationToken))
            {
                Name = "Production",
                IsBackground = false,
            };

            var consumptionThread = new Thread(() => _consumptionLoop.Run(cancellationToken))
            {
                Name = "Consumption",
                IsBackground = false,
            };

            productionThread.Start();
            consumptionThread.Start();

            productionThread.Join();
            consumptionThread.Join();
        }
        finally
        {
            foreach (var disposable in _disposables)
                disposable.Dispose();
        }
    }
}

/// <summary>
/// Factory for the default simulation engine configuration.
/// </summary>
internal static class SimulationEngine
{
    public static Engine<WorldSnapshot, SimulationProducer, SimulationConsumer<TRenderer>, TaskSaveRunner, TSaver, TClock, TWaiter>
        Create<TRenderer, TSaver, TClock, TWaiter>(
            TClock clock,
            TWaiter waiter,
            TSaver saver,
            TRenderer renderer,
            int simulationWorkerCount,
            int renderWorkerCount)
        where TRenderer : struct, IRenderer
        where TSaver : struct, ISaver<WorldSnapshot>
        where TClock : struct, IClock
        where TWaiter : struct, IWaiter
    {
        var nodePool = new ObjectPool<WorldSnapshot>(SimulationConstants.SnapshotPoolSize);
        var imagePool = new ObjectPool<WorldImage>(SimulationConstants.SnapshotPoolSize);
        var pinnedVersions = new PinnedVersions();
        var shared = new SharedState<WorldSnapshot>();
        var simulationCoordinator = new SimulationCoordinator(simulationWorkerCount);
        var renderCoordinator = new RenderCoordinator(renderWorkerCount);

        var producer = new SimulationProducer(imagePool, simulationCoordinator);
        var consumer = new SimulationConsumer<TRenderer>(renderCoordinator, renderer);

        return new Engine<WorldSnapshot, SimulationProducer, SimulationConsumer<TRenderer>, TaskSaveRunner, TSaver, TClock, TWaiter>(
            new ProductionLoop<WorldSnapshot, SimulationProducer, TClock, TWaiter>(
                nodePool, pinnedVersions, shared, producer, clock, waiter),
            new ConsumptionLoop<WorldSnapshot, SimulationConsumer<TRenderer>, TaskSaveRunner, TSaver, TClock, TWaiter>(
                pinnedVersions, shared, consumer, clock, waiter, new TaskSaveRunner(), saver),
            simulationCoordinator,
            renderCoordinator);
    }
}
