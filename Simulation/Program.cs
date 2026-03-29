using Simulation;
using Simulation.Engine;

using var cancellationSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellationSource.Cancel(); };

Engine<SystemClock, ThreadWaiter>.Create(new SystemClock(), new ThreadWaiter()).Run(cancellationSource.Token);
