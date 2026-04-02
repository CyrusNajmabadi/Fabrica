using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public sealed class ObjectPoolTests
{
    // ── Constructor ──────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_PreAllocatesRequestedNumberOfInstances()
    {
        var pool = new ObjectPool<Dummy, DummyAllocator>(5);

        Assert.Equal(5, pool.Available);
    }

    [Fact]
    public void Constructor_WithCapacityOne_CreatesUsablePool()
    {
        var pool = new ObjectPool<Dummy, DummyAllocator>(1);

        Assert.Equal(1, pool.Available);
        Assert.NotNull(pool.Rent());
        Assert.Equal(0, pool.Available);
    }

    // ── Rent basics ──────────────────────────────────────────────────────────

    [Fact]
    public void Rent_ReturnsCachedInstance_WhenPoolHasItems()
    {
        var pool = new ObjectPool<Dummy, DummyAllocator>(1);

        var item = pool.Rent();
        Assert.Equal(0, pool.Available);

        pool.Return(item);
        Assert.Equal(1, pool.Available);

        Assert.Same(item, pool.Rent());
    }

    [Fact]
    public void Rent_AllocatesNewInstance_WhenPoolIsExhausted()
    {
        var pool = new ObjectPool<Dummy, DummyAllocator>(2);

        var first = pool.Rent();
        var second = pool.Rent();
        var third = pool.Rent();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
        Assert.NotSame(first, second);
        Assert.NotSame(second, third);
        Assert.Equal(0, pool.Available);
    }

    // ── LIFO order ───────────────────────────────────────────────────────────

    [Fact]
    public void Rent_ReturnsLastReturnedFirst_LIFOOrder()
    {
        var pool = new ObjectPool<Dummy, DummyAllocator>(0);

        var a = new Dummy();
        var b = new Dummy();
        var c = new Dummy();

        pool.Return(a);
        pool.Return(b);
        pool.Return(c);

        Assert.Same(c, pool.Rent());
        Assert.Same(b, pool.Rent());
        Assert.Same(a, pool.Rent());
    }

    // ── Return / growth ──────────────────────────────────────────────────────

    [Fact]
    public void Return_GrowsPoolBeyondInitialCapacity()
    {
        var pool = new ObjectPool<Dummy, DummyAllocator>(1);

        var a = pool.Rent();
        Dummy b = new();
        Dummy c = new();

        pool.Return(a);
        pool.Return(b);
        pool.Return(c);

        Assert.Equal(3, pool.Available);

        var r1 = pool.Rent();
        var r2 = pool.Rent();
        var r3 = pool.Rent();

        Assert.Equal(0, pool.Available);
        Assert.Contains(a, new[] { r1, r2, r3 });
        Assert.Contains(b, new[] { r1, r2, r3 });
        Assert.Contains(c, new[] { r1, r2, r3 });
    }

    [Fact]
    public void Return_SameItemTwice_PoolsItTwice()
    {
        var pool = new ObjectPool<Dummy, DummyAllocator>(0);

        var item = new Dummy();
        pool.Return(item);
        pool.Return(item);

        Assert.Equal(2, pool.Available);

        var r1 = pool.Rent();
        var r2 = pool.Rent();
        Assert.Same(r1, r2);
        Assert.Same(item, r1);
    }

    // ── Sustained load cycles ────────────────────────────────────────────────

    [Fact]
    public void RentReturn_SustainedCycle_NeverExhaustsAndReusesInstances()
    {
        var pool = new ObjectPool<Dummy, DummyAllocator>(4);
        var seen = new HashSet<Dummy>(ReferenceEqualityComparer.Instance);

        for (var i = 0; i < 100; i++)
        {
            var item = pool.Rent();
            Assert.NotNull(item);
            seen.Add(item);
            pool.Return(item);
        }

        // Only a fixed number of distinct objects should have been allocated.
        Assert.True(seen.Count <= 5,
            $"Expected at most 5 distinct instances (4 pre-allocated + 1 extra), got {seen.Count}.");
    }

    [Fact]
    public void Rent_UnderBurstLoad_AllocatesAndRecoversThroughReturns()
    {
        var pool = new ObjectPool<Dummy, DummyAllocator>(2);
        var rented = new List<Dummy>();

        for (var i = 0; i < 20; i++)
            rented.Add(pool.Rent());

        Assert.Equal(0, pool.Available);
        Assert.Equal(20, rented.Count);

        foreach (var item in rented)
            pool.Return(item);

        Assert.Equal(20, pool.Available);

        for (var i = 0; i < 20; i++)
        {
            var item = pool.Rent();
            Assert.Contains(item, rented);
        }

        Assert.Equal(0, pool.Available);
    }

    [Fact]
    public void Available_AccuratelyTracksPoolDepth()
    {
        var pool = new ObjectPool<Dummy, DummyAllocator>(3);
        Assert.Equal(3, pool.Available);

        pool.Rent();
        Assert.Equal(2, pool.Available);

        pool.Rent();
        Assert.Equal(1, pool.Available);

        pool.Rent();
        Assert.Equal(0, pool.Available);

        pool.Rent();
        Assert.Equal(0, pool.Available);

        pool.Return(new Dummy());
        Assert.Equal(1, pool.Available);
    }

    private readonly struct DummyAllocator : IAllocator<Dummy>
    {
        public Dummy Allocate() => new();

        public void Reset(Dummy item) { }
    }

    private sealed class Dummy;
}
