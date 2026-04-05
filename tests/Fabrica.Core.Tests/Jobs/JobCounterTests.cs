using Fabrica.Core.Jobs;
using Xunit;

namespace Fabrica.Core.Tests.Jobs;

public class JobCounterTests
{
    // ── IsComplete ────────────────────────────────────────────────────────

    [Fact]
    public void ZeroCount_IsImmediatelyComplete()
    {
        var counter = new JobCounter(0);
        Assert.True(counter.IsComplete);
    }

    [Fact]
    public void NonZeroCount_IsNotComplete()
    {
        var counter = new JobCounter(3);
        Assert.False(counter.IsComplete);
    }

    [Fact]
    public void DefaultCounter_IsComplete()
    {
        var counter = default(JobCounter);
        Assert.True(counter.IsComplete);
    }

    // ── Decrement ─────────────────────────────────────────────────────────

    [Fact]
    public void Decrement_FromOne_ReturnsTrue()
    {
        var counter = new JobCounter(1);
        Assert.True(counter.Decrement());
    }

    [Fact]
    public void Decrement_FromOne_MakesComplete()
    {
        var counter = new JobCounter(1);
        counter.Decrement();
        Assert.True(counter.IsComplete);
    }

    [Fact]
    public void Decrement_FromTwo_ReturnsFalseOnFirst()
    {
        var counter = new JobCounter(2);
        Assert.False(counter.Decrement());
    }

    [Fact]
    public void Decrement_FromTwo_ReturnsTrueOnSecond()
    {
        var counter = new JobCounter(2);
        counter.Decrement();
        Assert.True(counter.Decrement());
    }

    [Fact]
    public void Decrement_AllN_MakesComplete()
    {
        const int N = 10;
        var counter = new JobCounter(N);

        for (var i = 0; i < N - 1; i++)
        {
            Assert.False(counter.Decrement());
            Assert.False(counter.IsComplete);
        }

        Assert.True(counter.Decrement());
        Assert.True(counter.IsComplete);
    }

    // ── Concurrent decrement ──────────────────────────────────────────────

    [Fact]
    public void ConcurrentDecrement_ExactlyOneThreadObservesZero()
    {
        const int N = 100;
        var counter = new JobCounter(N);
        var zeroCount = 0;
        var barrier = new Barrier(N);

        var threads = new Thread[N];
        for (var i = 0; i < N; i++)
        {
            threads[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                if (counter.Decrement())
                    Interlocked.Increment(ref zeroCount);
            });
            threads[i].Start();
        }

        for (var i = 0; i < N; i++)
            threads[i].Join();

        Assert.True(counter.IsComplete);
        Assert.Equal(1, zeroCount);
    }

    [Fact]
    public void ConcurrentDecrement_Stress()
    {
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            const int N = 8;
            var counter = new JobCounter(N);
            var zeroCount = 0;
            var barrier = new Barrier(N);

            var threads = new Thread[N];
            for (var i = 0; i < N; i++)
            {
                threads[i] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    if (counter.Decrement())
                        Interlocked.Increment(ref zeroCount);
                });
                threads[i].Start();
            }

            for (var i = 0; i < N; i++)
                threads[i].Join();

            Assert.True(counter.IsComplete);
            Assert.Equal(1, zeroCount);
        }
    }
}
