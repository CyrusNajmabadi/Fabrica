using Fabrica.Core.Jobs;

namespace Fabrica.SampleGame.Benchmarks.Scale;

/// <summary>
/// No-op job used as a synchronization barrier between DAG phases. All jobs in phase N+1 depend
/// on this barrier, and this barrier depends on all jobs in phase N.
/// </summary>
internal sealed class BarrierJob : Job
{
    protected internal override void Execute(JobContext context) { }
    protected override void ResetState() { }
}
