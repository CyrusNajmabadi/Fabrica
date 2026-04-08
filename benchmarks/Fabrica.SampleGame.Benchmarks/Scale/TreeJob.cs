using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;

namespace Fabrica.SampleGame.Benchmarks.Scale;

internal abstract class TreeJob : Job
{
    internal Handle<BenchNode> ResultHead;

    protected override void ResetState() => ResultHead = default;
}
