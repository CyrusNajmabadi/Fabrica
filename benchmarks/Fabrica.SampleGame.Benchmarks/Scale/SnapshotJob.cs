using System.Runtime.CompilerServices;
using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;

namespace Fabrica.SampleGame.Benchmarks.Scale;

/// <summary>
/// Final-phase job that does CPU work AND allocates a chain of <see cref="BenchNode"/>s,
/// exercising the arena allocation path alongside compute.
/// </summary>
internal sealed class SnapshotJob : Job
{
    internal int[]? WorkArray;
    internal int Iterations;
    internal int Seed;
    internal ThreadLocalBuffer<BenchNode>[]? Buffers;
    internal int NodeCount;
    internal bool IsRoot;

    internal Handle<BenchNode> ResultHead;

    protected internal override void Execute(JobContext context)
    {
        var arr = WorkArray!;
        var len = arr.Length;
        var iterations = Iterations;
        var seed = Seed;

        for (var i = 0; i < iterations; i++)
        {
            var idx = (i + seed) & (len - 1);
            arr[idx] = HashMix(arr[idx], i);
        }

        var buf = Buffers![context.WorkerIndex];
        var next = Handle<BenchNode>.None;
        for (var i = NodeCount - 1; i >= 0; i--)
        {
            var h = buf.Allocate(isRoot: IsRoot && i == 0);
            buf[h] = new BenchNode { Next = next, Value = seed + i };
            next = h;
        }

        ResultHead = next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HashMix(int h, int i)
    {
        h ^= i;
        h *= unchecked((int)0x85EBCA6B);
        h ^= (int)((uint)h >> 13);
        h *= unchecked((int)0xC2B2AE35);
        h ^= (int)((uint)h >> 16);
        return h;
    }

    protected override void ResetState()
    {
        WorkArray = null;
        Iterations = 0;
        Seed = 0;
        Buffers = null;
        NodeCount = 0;
        IsRoot = false;
        ResultHead = default;
    }
}
