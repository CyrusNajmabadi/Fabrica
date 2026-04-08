using Fabrica.Core.Threading;
using Xunit;

namespace Fabrica.Core.Tests.Threading;

public sealed class FastRandTests
{
    [Fact]
    public void Next_ProducesNonZeroValues()
    {
        var rng = new FastRand(42);
        var allZero = true;
        for (var i = 0; i < 1000; i++)
        {
            if (rng.Next() != 0)
            {
                allZero = false;
                break;
            }
        }

        Assert.False(allZero);
    }

    [Fact]
    public void Next_DeterministicForSameSeed()
    {
        var a = new FastRand(12345);
        var b = new FastRand(12345);

        for (var i = 0; i < 100; i++)
            Assert.Equal(a.Next(), b.Next());
    }

    [Fact]
    public void Next_DifferentSeedsProduceDifferentSequences()
    {
        var a = new FastRand(1);
        var b = new FastRand(2);

        var same = 0;
        for (var i = 0; i < 100; i++)
        {
            if (a.Next() == b.Next())
                same++;
        }

        Assert.True(same < 5, $"Expected divergent sequences but {same}/100 values matched");
    }

    [Fact]
    public void NextN_ReturnsValuesInRange()
    {
        var rng = new FastRand(999);
        for (var i = 0; i < 10_000; i++)
        {
            var v = rng.NextN(16);
            Assert.True(v < 16, $"NextN(16) returned {v}");
        }
    }

    [Fact]
    public void NextN_One_AlwaysReturnsZero()
    {
        var rng = new FastRand(42);
        for (var i = 0; i < 100; i++)
            Assert.Equal(0u, rng.NextN(1));
    }

    [Fact]
    public void NextN_HitsAllBuckets()
    {
        var rng = new FastRand(7777);
        const uint N = 8;
        var seen = new bool[N];

        for (var i = 0; i < 1000; i++)
            seen[rng.NextN(N)] = true;

        for (var i = 0; i < (int)N; i++)
            Assert.True(seen[i], $"Bucket {i} was never hit in 1000 draws from NextN({N})");
    }

    [Fact]
    public void NextN_DistributionIsReasonablyUniform()
    {
        var rng = new FastRand(31415);
        const uint N = 16;
        const int Draws = 100_000;
        var counts = new int[N];

        for (var i = 0; i < Draws; i++)
            counts[rng.NextN(N)]++;

        var expected = Draws / (double)N;
        for (var i = 0; i < (int)N; i++)
        {
            var deviation = Math.Abs(counts[i] - expected) / expected;
            Assert.True(deviation < 0.10,
                $"Bucket {i}: count={counts[i]}, expected~{expected:F0}, deviation={deviation:P1}");
        }
    }

    [Fact]
    public void ZeroSeed_LowHalfForcedNonZero()
    {
        // If both state words were zero, xorshift degenerates to all-zeros.
        // The constructor forces _s1 != 0 when the low 32 bits of the seed are zero.
        var rng = new FastRand(0);
        var nonZero = false;
        for (var i = 0; i < 100; i++)
        {
            if (rng.Next() != 0)
            {
                nonZero = true;
                break;
            }
        }

        Assert.True(nonZero, "FastRand(0) should still produce non-zero output");
    }

    [Fact]
    public void DifferentWorkerSeeds_ProduceDistinctFirstValues()
    {
        // Simulates the per-worker seeding strategy: workerIndex * golden ratio.
        const int WorkerCount = 16;
        var firstValues = new uint[WorkerCount];

        for (var i = 0; i < WorkerCount; i++)
        {
            var rng = new FastRand((ulong)i * 0x9E3779B97F4A7C15);
            firstValues[i] = rng.NextN(16);
        }

        var distinct = new HashSet<uint>(firstValues);
        Assert.True(distinct.Count >= WorkerCount / 2,
            $"Expected at least {WorkerCount / 2} distinct first steal targets among {WorkerCount} workers, got {distinct.Count}");
    }
}
