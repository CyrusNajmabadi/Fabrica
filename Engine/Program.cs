using Engine;
using Engine.Hosting;
using Engine.Hosting.ConsoleHost;
using Engine.Threading;

using var cancellationSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellationSource.Cancel(); };

// Both simulation and render worker groups get access to all cores.
// When one group is idle (e.g. simulation is throttled by backpressure),
// the OS scheduler naturally gives the other group's threads full CPU time.
// Thread pinning is best-effort and overlapping — both groups pin to cores
// 0..N-1, relying on the OS to time-share when both are active.
var workerCount = Math.Max(1, Environment.ProcessorCount);

var saveIntervalNanoseconds = SimulationConstants.SaveIntervalTicks
                              * SimulationConstants.TickDurationNanoseconds;

SimulationEngine.Create(
    new SystemClock(),
    new ThreadWaiter(),
    new ConsoleRenderer(),
    workerCount,
    workerCount,
    new ConsoleSaveConsumer(saveIntervalNanoseconds)).Run(cancellationSource.Token);
