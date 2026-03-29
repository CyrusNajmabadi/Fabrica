using Simulation;
using Simulation.Engine;

using var cancellationSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellationSource.Cancel(); };

Engine<SystemClock>.Create(new SystemClock()).Run(cancellationSource.Token);
