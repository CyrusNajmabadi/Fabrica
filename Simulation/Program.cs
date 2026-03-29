using Simulation;
using Simulation.Engine;
using Simulation.Memory;

var memory = new MemorySystem(SimulationConstants.SnapshotPoolSize);
var shared = new SharedState();
var clock  = new SystemClock();

var simulationLoop  = new SimulationLoop(memory, shared, clock);
var consumptionLoop = new ConsumptionLoop(memory, shared, clock);

using var cancellationSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellationSource.Cancel(); };

var simulationThread = new Thread(() => simulationLoop.Run(cancellationSource.Token))
{
    Name         = "Simulation",
    IsBackground = false,
};

var consumptionThread = new Thread(() => consumptionLoop.Run(cancellationSource.Token))
{
    Name         = "Consumption",
    IsBackground = false,
};

simulationThread.Start();
consumptionThread.Start();

simulationThread.Join();
consumptionThread.Join();
