using Simulation;
using Simulation.Engine;

using var cancellationSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellationSource.Cancel(); };

Engine<SystemClock, ThreadWaiter, TaskSaveRunner, ConsoleSaver, ConsoleRenderer>.Create(
    new SystemClock(),
    new ThreadWaiter(),
    new TaskSaveRunner(),
    new ConsoleSaver(),
    new ConsoleRenderer()).Run(cancellationSource.Token);
