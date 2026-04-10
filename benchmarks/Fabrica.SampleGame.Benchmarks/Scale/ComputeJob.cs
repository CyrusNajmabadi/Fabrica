using System.Diagnostics;
using System.Runtime.CompilerServices;
#if UNSAFE_OPT
using System.Runtime.InteropServices;
#endif
using Fabrica.Core.Jobs;

namespace Fabrica.SampleGame.Benchmarks.Scale;

/// <summary>
/// Job that simulates realistic CPU work via a tight hash-mixing loop over a per-job local
/// array that fits in L1 cache. The iteration count controls how long the job runs.
/// Each job owns its own array to avoid false sharing between cores.
/// </summary>
internal sealed class ComputeJob(JobScheduler scheduler) : Job(scheduler)
{
    private const int ArrayLength = 64;

    /// <summary>Per-job work array (64 ints = 256 bytes, fits in L1 cache). Avoids false sharing.</summary>
    private readonly int[] _localArray = new int[ArrayLength];

    /// <summary>Number of hash-mixing iterations. Controls job duration.</summary>
    internal int Iterations;

    /// <summary>Per-job seed to prevent the optimizer from merging identical jobs.</summary>
    internal int Seed;

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

#if UNSAFE_OPT
        Debug.Assert(arr.Length is ArrayLength && (arr.Length & mask) == 0);
        ref var r0 = ref MemoryMarshal.GetArrayDataReference(arr);

        for (var i = 0; i < iterations; i++)
        {
            var idx = (i + seed) & mask;
            ref var slot = ref Unsafe.Add(ref r0, idx);
            slot = HashMix(slot, i);
        }
#else
        for (var i = 0; i < iterations; i++)
        {
            var idx = (i + seed) & mask;
            arr[idx] = HashMix(arr[idx], i);
        }
#endif

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
        StartTimestamp = 0;
        EndTimestamp = 0;
    }
}
