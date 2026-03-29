using Simulation.Memory;
using Xunit;

namespace Simulation.Tests.Memory;

public sealed class ObjectPoolTests
{
    [Fact]
    public void Rent_ReturnsNull_WhenPoolIsExhausted()
    {
        var pool = new ObjectPool<Dummy>(2);

        Dummy? first = pool.Rent();
        Dummy? second = pool.Rent();
        Dummy? third = pool.Rent();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(third);
        Assert.Equal(0, pool.Available);
        Assert.Equal(2, pool.Capacity);
    }

    [Fact]
    public void Return_MakesItemAvailableAgain()
    {
        var pool = new ObjectPool<Dummy>(1);

        Dummy item = Assert.IsType<Dummy>(pool.Rent());
        Assert.Equal(0, pool.Available);

        pool.Return(item);

        Assert.Equal(1, pool.Available);
        Assert.Same(item, pool.Rent());
    }

    private sealed class Dummy
    {
    }
}
