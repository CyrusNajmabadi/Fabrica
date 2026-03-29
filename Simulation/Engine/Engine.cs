using Simulation.Memory;

namespace Simulation.Engine;

/// <summary>
/// Top-level coordinator: owns the simulation and consumption loops and manages
/// their thread lifetimes.
///
/// ════════════════════════ TWO-THREAD DESIGN OVERVIEW ════════════════════════
///
///  SIMULATION THREAD  (producer / memory owner)                   40 ticks/sec
///  ──────────────────────────────────────────────────────────────────────────
///  Bootstrap() → allocates tick-0 snapshot and publishes it
///  Tick loop:
///    • Advance world state into a fresh WorldImage
///    • Append new WorldSnapshot onto the forward chain:
///        [tick 0] → [tick 1] → [tick 2] → … → [tick N]
///         _oldest                                _current / LatestSnapshot
///    • Volatile-write LatestSnapshot = [tick N]  (release fence)
///    • CleanupStaleSnapshots(): walk chain from _oldest, freeing every
///      snapshot whose tick &lt; ConsumptionEpoch and is not pinned
///    • ApplyPressureDelay(): slow down if pool is under pressure
///
///  CONSUMPTION THREAD  (renderer / save coordinator)              ≈60 fps
///  ──────────────────────────────────────────────────────────────────────────
///  Frame loop:
///    • Volatile-read LatestSnapshot                (acquire fence)
///    • MaybeStartSave(): if a save is due, pin the snapshot and hand it
///      off to the save task before advancing the epoch
///    • Render(previous, current): always called, even if snapshot unchanged
///    • Volatile-write ConsumptionEpoch = tick      (release fence)
///    • ThrottleToFrameRate()
///
///  SAVE TASK  (threadpool — spawned by consumption thread)
///  ──────────────────────────────────────────────────────────────────────────
///    • Saver.Save(image, tick)
///    • PinnedVersions.Unpin(tick)  ← releases the simulation's hold
///    • NextSaveAtTick = tick + SaveIntervalTicks
///
/// ══════════════════════════════ SHARED STATE ════════════════════════════════
///
///  Exactly three volatile fields cross the thread boundary:
///
///  SharedState.LatestSnapshot   (volatile WorldSnapshot?)
///    Written by simulation only; read by consumption.
///    The volatile release/acquire pair guarantees that all WorldImage writes
///    made before the publish are visible to any thread that reads the snapshot.
///    No additional synchronisation is needed to access Image fields.
///
///  SharedState.ConsumptionEpoch  (volatile int)
///    Written by consumption only; read by simulation.
///    Simulation frees tick &lt; epoch.  Conservative race: if simulation reads
///    a stale (lower) epoch it retains a snapshot one extra cleanup pass — it
///    never frees something the consumption thread is still touching.
///
///  SharedState.NextSaveAtTick   (volatile int)
///    Written by consumption (sets to 0) AND the save task (sets next interval).
///    The two writes do not conflict because consumption only writes 0 while
///    the field is non-zero, and the save task writes non-zero later from
///    a different execution context.
///
/// ══════════════════════════ WHY NO LOCKS IN THE HOT PATH ════════════════════
///
///  Each field above has at most one writer thread in the hot path.
///  Volatile fences provide the required visibility across CPUs.
///  The epoch is conservative, so races can only make the system hold memory
///  slightly longer — they can never corrupt state or free a live object.
///
///  The sole exception is PinnedVersions (snapshot pinning for saves), which
///  uses ConcurrentDictionary because Unpin arrives from a threadpool save
///  task.  Pinning only happens at save boundaries (≈every 5 minutes of game
///  time), so it is not on the hot path.  See PinnedVersions for details.
///
/// ════════════════════════════════════════════════════════════════════════════
///
/// Use <see cref="Create"/> for the default production configuration.
/// Use the explicit constructor to inject custom loops (e.g. in tests).
/// </summary>
internal sealed class Engine<TClock, TWaiter, TSaveRunner, TSaver, TRenderer>
    where TClock : struct, IClock
    where TWaiter : struct, IWaiter
    where TSaveRunner : struct, ISaveRunner
    where TSaver : struct, ISaver
    where TRenderer : struct, IRenderer
{
    private readonly SimulationLoop<TClock, TWaiter> _simulationLoop;
    private readonly ConsumptionLoop<TClock, TWaiter, TSaveRunner, TSaver, TRenderer> _consumptionLoop;

    public Engine(
        SimulationLoop<TClock, TWaiter> simulationLoop,
        ConsumptionLoop<TClock, TWaiter, TSaveRunner, TSaver, TRenderer> consumptionLoop)
    {
        _simulationLoop  = simulationLoop;
        _consumptionLoop = consumptionLoop;
    }

    /// <summary>
    /// Builds a fully wired engine with default pool sizes and the supplied clock.
    /// </summary>
    public static Engine<TClock, TWaiter, TSaveRunner, TSaver, TRenderer> Create(
        TClock clock,
        TWaiter waiter,
        TSaveRunner saveRunner,
        TSaver saver,
        TRenderer renderer)
    {
        var memory = new MemorySystem(SimulationConstants.SnapshotPoolSize);
        var shared = new SharedState();

        return new Engine<TClock, TWaiter, TSaveRunner, TSaver, TRenderer>(
            new SimulationLoop<TClock, TWaiter>(memory, shared, clock, waiter),
            new ConsumptionLoop<TClock, TWaiter, TSaveRunner, TSaver, TRenderer>(
                memory,
                shared,
                clock,
                waiter,
                saveRunner,
                saver,
                renderer));
    }

    /// <summary>
    /// Starts both loops on dedicated threads and blocks until both exit.
    /// </summary>
    public void Run(CancellationToken cancellationToken)
    {
        var simulationThread = new Thread(() => _simulationLoop.Run(cancellationToken))
        {
            Name         = "Simulation",
            IsBackground = false,
        };

        var consumptionThread = new Thread(() => _consumptionLoop.Run(cancellationToken))
        {
            Name         = "Consumption",
            IsBackground = false,
        };

        simulationThread.Start();
        consumptionThread.Start();

        simulationThread.Join();
        consumptionThread.Join();
    }
}
