using System.Diagnostics;
using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;

namespace Fabrica.SampleGame.Benchmarks.Scale;

/// <summary>
/// Fan-in job that collects results from <see cref="SnapshotJob"/>s into a single root node.
/// </summary>
internal sealed class SnapshotCollectorJob(JobScheduler scheduler) : Job(scheduler)
{
    internal ThreadLocalBuffer<BenchNode>[]? Buffers;
    internal SnapshotJob[] Sources = null!;
    internal Handle<BenchNode> ResultHead;

    internal bool Instrument;
    internal long StartTimestamp;
    internal long EndTimestamp;

    protected internal override void Execute(JobContext context)
    {
        if (Instrument) StartTimestamp = Stopwatch.GetTimestamp();
        var buf = Buffers![context.WorkerIndex];
        var node = new BenchNode();

        if (Sources.Length > 0) node.Child0 = Sources[0].ResultHead;
        if (Sources.Length > 1) node.Child1 = Sources[1].ResultHead;
        if (Sources.Length > 2) node.Child2 = Sources[2].ResultHead;
        if (Sources.Length > 3) node.Child3 = Sources[3].ResultHead;
        if (Sources.Length > 4) node.Child4 = Sources[4].ResultHead;
        if (Sources.Length > 5) node.Child5 = Sources[5].ResultHead;
        if (Sources.Length > 6) node.Child6 = Sources[6].ResultHead;
        if (Sources.Length > 7) node.Child7 = Sources[7].ResultHead;

        if (Sources.Length > 8)
        {
            var next = Handle<BenchNode>.None;
            for (var i = Sources.Length - 1; i >= 8; i--)
            {
                var overflow = buf.Allocate(new BenchNode { Child0 = Sources[i].ResultHead, Next = next });
                next = overflow;
            }

            node.Next = next;
        }

        var h = buf.Allocate(node, isRoot: true);
        ResultHead = h;
        if (Instrument) EndTimestamp = Stopwatch.GetTimestamp();
    }

    protected override void ResetState()
    {
        Buffers = null;
        ResultHead = default;
        StartTimestamp = 0;
        EndTimestamp = 0;
    }
}
