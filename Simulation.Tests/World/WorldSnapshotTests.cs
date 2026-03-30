using Simulation.World;
using Xunit;

namespace Simulation.Tests.World;

/// <summary>
/// Tests for <see cref="WorldSnapshot"/>-specific behavior (domain payload).
/// Chain mechanics are covered by <see cref="ChainNodeTests"/>.
/// </summary>
public sealed class WorldSnapshotTests
{
    [Fact]
    public void Initialize_SetsImageAndTickNumber()
    {
        var image = new WorldImage();
        var snapshot = new WorldSnapshot();

        snapshot.Initialize(image, tickNumber: 5);

        Assert.Same(image, snapshot.Image);
        Assert.Equal(5, snapshot.TickNumber);
        Assert.Equal(5, snapshot.SequenceNumber);
    }

    [Fact]
    public void TickNumber_IsDomainAliasForSequenceNumber()
    {
        var snapshot = new WorldSnapshot();
        snapshot.Initialize(new WorldImage(), tickNumber: 42);

        Assert.Equal(snapshot.SequenceNumber, snapshot.TickNumber);
    }

    [Fact]
    public void Release_ToZero_NullsImage()
    {
        var image = new WorldImage();
        var snapshot = new WorldSnapshot();
        snapshot.Initialize(image, tickNumber: 10);

        snapshot.Release();

        Assert.True(snapshot.IsUnreferenced);
        Assert.Null(snapshot.Image);
    }

    [Fact]
    public void AddRef_PreventsRelease_FromNullingImage()
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
    public void AddRef_ThenFullRelease_NullsImage()
    {
        var snapshot = new WorldSnapshot();
        snapshot.Initialize(new WorldImage(), tickNumber: 5);

        snapshot.AddRef();
        snapshot.Release();
        snapshot.Release();

        Assert.True(snapshot.IsUnreferenced);
        Assert.Null(snapshot.Image);
    }
}
