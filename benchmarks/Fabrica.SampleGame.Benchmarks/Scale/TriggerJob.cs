using Fabrica.Core.Jobs;

namespace Fabrica.SampleGame.Benchmarks.Scale;

internal sealed class TriggerJob : Job
{
    protected internal override void Execute(JobContext context) { }
    protected override void ResetState() { }
}
