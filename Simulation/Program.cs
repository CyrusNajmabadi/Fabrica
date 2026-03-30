using Simulation;
using Simulation.Engine;

using var cancellationSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellationSource.Cancel(); };

// Split available cores between simulation workers and render workers,
// reserving one core for the consumption thread (renderer + save coordinator).
// The simulation loop thread itself is idle while workers run (parked on
// WaitForCompletion), so it doesn't need a dedicated core.
var availableCores = Math.Max(2, Environment.ProcessorCount - 1);
var simulationWorkerCount = Math.Max(1, availableCores / 2);
var renderWorkerCount = Math.Max(1, availableCores - simulationWorkerCount);

Engine<SystemClock, ThreadWaiter, TaskSaveRunner, ConsoleSaver, ConsoleRenderer>.Create(
    new SystemClock(),
    new ThreadWaiter(),
    new TaskSaveRunner(),
    new ConsoleSaver(),
    new ConsoleRenderer(),
    simulationWorkerCount,
    renderWorkerCount).Run(cancellationSource.Token);
