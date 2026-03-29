using Simulation;
using Simulation.Engine;
using Simulation.Memory;

var memory = new MemorySystem(SimConstants.SnapshotPoolSize);
var shared = new SharedState();

var simLoop         = new SimulationLoop(memory, shared);
var consumptionLoop = new ConsumptionLoop(shared, memory);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var simThread = new Thread(() => simLoop.Run(cts.Token))
{
    Name         = "Simulation",
    IsBackground = false,
};

var consumptionThread = new Thread(() => consumptionLoop.Run(cts.Token))
{
    Name         = "Consumption",
    IsBackground = false,
};

simThread.Start();
consumptionThread.Start();

simThread.Join();
consumptionThread.Join();
