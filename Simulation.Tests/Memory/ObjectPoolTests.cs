using Simulation.Memory;
using Xunit;

namespace Simulation.Tests.Memory;

public sealed class ObjectPoolTests
{
    [Fact]
    public void Rent_AllocatesNewInstance_WhenPoolIsExhausted()
    {
        var pool = new ObjectPool<Dummy>(2);

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

    [Fact]
    public void Rent_ReturnsCachedInstance_WhenPoolHasItems()
    {
        var pool = new ObjectPool<Dummy>(1);

        var item = pool.Rent();
        Assert.Equal(0, pool.Available);

        pool.Return(item);
        Assert.Equal(1, pool.Available);

        Assert.Same(item, pool.Rent());
    }

    [Fact]
    public void Return_GrowsPoolBeyondInitialCapacity()
    {
        var pool = new ObjectPool<Dummy>(1);

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
    public void Constructor_PreAllocatesRequestedNumberOfInstances()
    {
        var pool = new ObjectPool<Dummy>(5);

        Assert.Equal(5, pool.Available);
    }

    private sealed class Dummy
    {
    }
}
