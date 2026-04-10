using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;

namespace Fabrica.SampleGame.Benchmarks.Scale;

internal sealed class LeafJob(JobScheduler scheduler) : TreeJob(scheduler)
{
    internal ThreadLocalBuffer<BenchNode>[]? Buffers;
    internal int ChainLength;

    protected internal override void Execute(JobContext context)
    {
        var buf = Buffers![context.WorkerIndex];
        var next = Handle<BenchNode>.None;

        for (var i = ChainLength - 1; i >= 0; i--)
        {
            var h = buf.Allocate(new BenchNode { Next = next, Value = i });
            next = h;
        }

        ResultHead = next;
    }

    protected override void ResetState()
    {
        base.ResetState();
        Buffers = null;
    }
}
