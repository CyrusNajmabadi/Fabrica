using System.Diagnostics;
using Fabrica.Core.Jobs;

namespace Fabrica.SampleGame.Benchmarks.Scale;

internal sealed class TriggerJob(JobScheduler scheduler) : Job(scheduler)
{
    internal bool Instrument;
    internal long ExecutedTimestamp;

    protected internal override void Execute(JobContext context)
    {
        if (Instrument) ExecutedTimestamp = Stopwatch.GetTimestamp();
    }

    protected override void ResetState() =>
        ExecutedTimestamp = 0;
}
