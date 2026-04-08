using System.Runtime.CompilerServices;
using Fabrica.Core.Jobs;

namespace Fabrica.SampleGame.Benchmarks.Scale;

/// <summary>
/// Job that simulates realistic CPU work via a tight hash-mixing loop over a small array that
/// fits in L1 cache. The iteration count controls how long the job runs (~30-40μs at the
/// calibrated value). Subclasses or direct configuration sets <see cref="Iterations"/>.
/// </summary>
internal sealed class ComputeJob : Job
{
    /// <summary>Shared work array (64 ints = 256 bytes, fits in a single cache line pair).</summary>
    internal int[]? WorkArray;

    /// <summary>Number of hash-mixing iterations. Controls job duration.</summary>
    internal int Iterations;

    /// <summary>Per-job seed to prevent the optimizer from merging identical jobs.</summary>
    internal int Seed;

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
    }
}
