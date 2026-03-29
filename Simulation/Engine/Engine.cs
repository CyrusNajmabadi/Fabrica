using Simulation.Memory;

namespace Simulation.Engine;

/// <summary>
/// Top-level coordinator: owns the simulation and consumption loops and manages
/// their thread lifetimes.
///
/// Use <see cref="Create"/> for the default production configuration.
/// Use the explicit constructor to inject custom loops (e.g. in tests).
/// </summary>
internal sealed class Engine<TClock> where TClock : struct, IClock
{
    private readonly SimulationLoop<TClock>  _simulationLoop;
    private readonly ConsumptionLoop<TClock> _consumptionLoop;

    public Engine(SimulationLoop<TClock> simulationLoop, ConsumptionLoop<TClock> consumptionLoop)
    {
        _simulationLoop  = simulationLoop;
        _consumptionLoop = consumptionLoop;
    }

    /// <summary>
    /// Builds a fully wired engine with default pool sizes and the supplied clock.
    /// </summary>
    public static Engine<TClock> Create(TClock clock)
    {
        var memory = new MemorySystem(SimulationConstants.SnapshotPoolSize);
        var shared = new SharedState();

        return new Engine<TClock>(
            new SimulationLoop<TClock>(memory, shared, clock),
            new ConsumptionLoop<TClock>(memory, shared, clock));
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
