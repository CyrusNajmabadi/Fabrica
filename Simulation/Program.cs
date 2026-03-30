using Simulation;
using Simulation.Engine;

using var cancellationSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellationSource.Cancel(); };

// Reserve one core for the consumption thread (renderer + save coordinator).
// The simulation loop thread itself is idle while workers run (parked on
// WaitForCompletion), so it doesn't need a dedicated core.  Giving workers
// access to all remaining cores lets them saturate available parallelism;
// backpressure ensures the simulation slows down if rendering needs to
// catch up, so the two never fight over CPU in practice.
var workerCount = Math.Max(1, Environment.ProcessorCount - 1);

Engine<SystemClock, ThreadWaiter, TaskSaveRunner, ConsoleSaver, ConsoleRenderer>.Create(
    new SystemClock(),
    new ThreadWaiter(),
    new TaskSaveRunner(),
    new ConsoleSaver(),
    new ConsoleRenderer(),
    workerCount).Run(cancellationSource.Token);
