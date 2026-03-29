using Simulation.Memory;
using Xunit;

namespace Simulation.Tests.Memory;

public sealed class MemorySystemTests
{
    [Fact]
    public void Constructor_RejectsPoolSizesThatAreNotMultiplesOfEight()
    {
        var exception = Assert.Throws<ArgumentException>(() => new MemorySystem(poolSize: 10));
        Assert.Contains("multiple of 8", exception.Message);
    }

    [Fact]
    public void Constructor_AcceptsPoolSizesThatAreMultiplesOfEight()
    {
        var memory = new MemorySystem(poolSize: 16);

        Assert.Equal(16, memory.SnapshotPoolCapacity);
        Assert.Equal(16, memory.SnapshotsAvailable);
    }
}
