using System.Diagnostics;
using System.Runtime.CompilerServices;
#if !DEBUG
using System.Runtime.InteropServices;
#endif
using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;

namespace Fabrica.SampleGame.Benchmarks.Scale;

/// <summary>
/// Final-phase job that does CPU work AND allocates a chain of <see cref="BenchNode"/>s,
/// exercising the arena allocation path alongside compute.
/// </summary>
internal sealed class SnapshotJob : Job
{
    private const int ArrayLength = 64;
    private readonly int[] _localArray = new int[ArrayLength];

    internal int Iterations;
    internal int Seed;
    internal ThreadLocalBuffer<BenchNode>[]? Buffers;
    internal int NodeCount;
    internal bool IsRoot;

    internal Handle<BenchNode> ResultHead;

    internal bool Instrument;
    internal long StartTimestamp;
    internal long EndTimestamp;

    protected internal override void Execute(JobContext context)
    {
        if (Instrument) StartTimestamp = Stopwatch.GetTimestamp();
        var arr = _localArray;
        var mask = arr.Length - 1;
        var iterations = Iterations;
        var seed = Seed;

#if DEBUG
        for (var i = 0; i < iterations; i++)
        {
            var idx = (i + seed) & mask;
            arr[idx] = HashMix(arr[idx], i);
        }
#else
        Debug.Assert(arr.Length is ArrayLength && (arr.Length & mask) == 0);
        ref var r0 = ref MemoryMarshal.GetArrayDataReference(arr);

        for (var i = 0; i < iterations; i++)
        {
            var idx = (i + seed) & mask;
            ref var slot = ref Unsafe.Add(ref r0, idx);
            slot = HashMix(slot, i);
        }
#endif

        var buf = Buffers![context.WorkerIndex];
        var next = Handle<BenchNode>.None;
        for (var i = NodeCount - 1; i >= 0; i--)
        {
            var h = buf.Allocate(isRoot: IsRoot && i == 0);
            buf[h] = new BenchNode { Next = next, Value = seed + i };
            next = h;
        }

        ResultHead = next;
        if (Instrument) EndTimestamp = Stopwatch.GetTimestamp();
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
        Iterations = 0;
        Seed = 0;
        Buffers = null;
        NodeCount = 0;
        IsRoot = false;
        ResultHead = default;
        StartTimestamp = 0;
        EndTimestamp = 0;
    }
}
