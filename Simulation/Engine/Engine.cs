using Simulation.Memory;

namespace Simulation.Engine;

/// <summary>
/// Top-level coordinator: owns the simulation and consumption loops and manages
/// their thread lifetimes.
///
/// Use <see cref="Create"/> for the default production configuration.
/// Use the explicit constructor to inject custom loops (e.g. in tests).
/// </summary>
internal sealed class Engine<TClock, TWaiter, TSaveRunner, TSaver>
    where TClock : struct, IClock
    where TWaiter : struct, IWaiter
    where TSaveRunner : struct, ISaveRunner
    where TSaver : struct, ISaver
{
    private readonly SimulationLoop<TClock, TWaiter> _simulationLoop;
    private readonly ConsumptionLoop<TClock, TWaiter, TSaveRunner, TSaver> _consumptionLoop;

    public Engine(
        SimulationLoop<TClock, TWaiter> simulationLoop,
        ConsumptionLoop<TClock, TWaiter, TSaveRunner, TSaver> consumptionLoop)
    {
        _simulationLoop  = simulationLoop;
        _consumptionLoop = consumptionLoop;
    }

    /// <summary>
    /// Builds a fully wired engine with default pool sizes and the supplied clock.
    /// </summary>
    public static Engine<TClock, TWaiter, TSaveRunner, TSaver> Create(
        TClock clock,
        TWaiter waiter,
        TSaveRunner saveRunner,
        TSaver saver)
    {
        var memory = new MemorySystem(SimulationConstants.SnapshotPoolSize);
        var shared = new SharedState();

        return new Engine<TClock, TWaiter, TSaveRunner, TSaver>(
            new SimulationLoop<TClock, TWaiter>(memory, shared, clock, waiter),
            new ConsumptionLoop<TClock, TWaiter, TSaveRunner, TSaver>(
                memory,
                shared,
                clock,
                waiter,
                saveRunner,
                saver));
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
