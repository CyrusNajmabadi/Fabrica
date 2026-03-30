using Simulation.Memory;
using Xunit;

namespace Simulation.Tests.Memory;

public sealed class MemorySystemTests
{
    [Fact]
    public void Constructor_RejectsNonPositivePoolSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemorySystem(initialPoolSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemorySystem(initialPoolSize: -1));
    }

    [Fact]
    public void Constructor_AcceptsAnyPositivePoolSize()
    {
        var memory = new MemorySystem(initialPoolSize: 10);

        var snapshot = memory.RentSnapshot();
        var image = memory.RentImage();

        Assert.NotNull(snapshot);
        Assert.NotNull(image);
    }

    [Fact]
    public void RentSnapshot_AlwaysReturnsNonNull()
    {
        var memory = new MemorySystem(initialPoolSize: 1);

        var first = memory.RentSnapshot();
        var second = memory.RentSnapshot();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void RentImage_AlwaysReturnsNonNull()
    {
        var memory = new MemorySystem(initialPoolSize: 1);

        var first = memory.RentImage();
        var second = memory.RentImage();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }
}
