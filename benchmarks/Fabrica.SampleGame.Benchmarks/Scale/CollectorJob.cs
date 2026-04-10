using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;

namespace Fabrica.SampleGame.Benchmarks.Scale;

internal sealed class CollectorJob(JobScheduler scheduler) : TreeJob(scheduler)
{
    internal ThreadLocalBuffer<BenchNode>[]? Buffers;
    internal TreeJob[] Children = null!;
    internal bool IsRoot;

    protected internal override void Execute(JobContext context)
    {
        var buf = Buffers![context.WorkerIndex];
        var h = buf.Allocate(new BenchNode
        {
            Child0 = Children[0].ResultHead,
            Child1 = Children[1].ResultHead,
            Child2 = Children[2].ResultHead,
            Child3 = Children[3].ResultHead,
            Child4 = Children[4].ResultHead,
            Child5 = Children[5].ResultHead,
            Child6 = Children[6].ResultHead,
            Child7 = Children[7].ResultHead,
        }, isRoot: IsRoot);
        ResultHead = h;
    }

    protected override void ResetState()
    {
        base.ResetState();
        Buffers = null;
    }
}
