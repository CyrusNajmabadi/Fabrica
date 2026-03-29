using Simulation.World;
using Xunit;

namespace Simulation.Tests.World;

public sealed class WorldSnapshotTests
{
    [Fact]
    public void Initialize_SetsImageAndClearsNext()
    {
        var image = new WorldImage { TickNumber = 5 };
        var snapshot = new WorldSnapshot();

        snapshot.Initialize(image);

        Assert.Same(image, snapshot.Image);
        Assert.Null(snapshot.Next);
        Assert.False(snapshot.IsUnreferenced);
    }

    [Fact]
    public void SetNext_LinksSnapshots()
    {
        var first = new WorldSnapshot();
        var second = new WorldSnapshot();

        first.Initialize(new WorldImage());
        second.Initialize(new WorldImage());

        first.SetNext(second);

        Assert.Same(second, first.Next);
    }

    [Fact]
    public void ClearNext_RemovesLink()
    {
        var first = new WorldSnapshot();
        var second = new WorldSnapshot();

        first.Initialize(new WorldImage());
        second.Initialize(new WorldImage());
        first.SetNext(second);

        first.ClearNext();

        Assert.Null(first.Next);
    }

    [Fact]
    public void Release_ToZero_ClearsReferences()
    {
        var image = new WorldImage { TickNumber = 10 };
        var next = new WorldSnapshot();
        var snapshot = new WorldSnapshot();

        snapshot.Initialize(image);
        next.Initialize(new WorldImage());
        snapshot.SetNext(next);

        snapshot.Release();

        Assert.True(snapshot.IsUnreferenced);
        Assert.Null(snapshot.Next);
        Assert.Null(snapshot.Image);
    }
}
