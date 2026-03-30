using Simulation.World;
using Xunit;

namespace Simulation.Tests.World;

public sealed class WorldSnapshotTests
{
    [Fact]
    public void Initialize_SetsImageAndTickAndClearsNext()
    {
        var image = new WorldImage();
        var snapshot = new WorldSnapshot();

        snapshot.Initialize(image, tickNumber: 5);

        Assert.Same(image, snapshot.Image);
        Assert.Equal(5, snapshot.TickNumber);
        Assert.Null(snapshot.Next);
        Assert.False(snapshot.IsUnreferenced);
    }

    [Fact]
    public void SetNext_LinksSnapshots()
    {
        var first = new WorldSnapshot();
        var second = new WorldSnapshot();

        first.Initialize(new WorldImage(), 0);
        second.Initialize(new WorldImage(), 1);

        first.SetNext(second);

        Assert.Same(second, first.Next);
    }

    [Fact]
    public void ClearNext_RemovesLink()
    {
        var first = new WorldSnapshot();
        var second = new WorldSnapshot();

        first.Initialize(new WorldImage(), 0);
        second.Initialize(new WorldImage(), 1);
        first.SetNext(second);

        first.ClearNext();

        Assert.Null(first.Next);
    }

    [Fact]
    public void Initialize_ClearsPublishTime()
    {
        var snapshot = new WorldSnapshot();
        snapshot.Initialize(new WorldImage(), 0);
        snapshot.MarkPublished(999);
        snapshot.Release();

        snapshot.Initialize(new WorldImage(), 1);

        Assert.Equal(0, snapshot.PublishTimeNanoseconds);
    }

    [Fact]
    public void MarkPublished_SetsPublishTime()
    {
        var snapshot = new WorldSnapshot();
        snapshot.Initialize(new WorldImage(), 0);

        snapshot.MarkPublished(42_000_000);

        Assert.Equal(42_000_000, snapshot.PublishTimeNanoseconds);
    }

    [Fact]
    public void AddRef_PreventsReleaseFromClearingReferences()
    {
        var image = new WorldImage();
        var snapshot = new WorldSnapshot();
        snapshot.Initialize(image, tickNumber: 5);

        snapshot.AddRef();
        snapshot.Release();

        Assert.False(snapshot.IsUnreferenced);
        Assert.Same(image, snapshot.Image);
    }

    [Fact]
    public void AddRef_ThenFullRelease_ClearsReferences()
    {
        var snapshot = new WorldSnapshot();
        snapshot.Initialize(new WorldImage(), tickNumber: 5);

        snapshot.AddRef();
        snapshot.Release();
        snapshot.Release();

        Assert.True(snapshot.IsUnreferenced);
        Assert.Null(snapshot.Image);
    }

    [Fact]
    public void Release_ToZero_ClearsReferences()
    {
        var image = new WorldImage();
        var next = new WorldSnapshot();
        var snapshot = new WorldSnapshot();

        snapshot.Initialize(image, tickNumber: 10);
        next.Initialize(new WorldImage(), 0);
        snapshot.SetNext(next);

        snapshot.Release();

        Assert.True(snapshot.IsUnreferenced);
        Assert.Null(snapshot.Next);
        Assert.Null(snapshot.Image);
    }
}
