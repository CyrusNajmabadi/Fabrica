using Simulation.World;
using Xunit;

namespace Simulation.Tests.World;

/// <summary>
/// Tests for <see cref="ChainNode{WorldImage}"/> payload lifecycle — setting,
/// clearing, and ref-count interaction with payload visibility.
/// </summary>
public sealed class WorldSnapshotTests
{
    [Fact]
    public void InitializeBase_AndPayload_SetsCorrectly()
    {
        var image = new WorldImage();
        var node = new ChainNode<WorldImage>();

        node.InitializeBase(5);
        node.Payload = image;

        Assert.Same(image, node.Payload);
        Assert.Equal(5, node.SequenceNumber);
    }

    [Fact]
    public void Release_ToZero_AllowsPayloadClearing()
    {
        var image = new WorldImage();
        var node = new ChainNode<WorldImage>();
        node.InitializeBase(10);
        node.Payload = image;

        node.ClearPayload();
        node.Release();

        Assert.True(node.IsUnreferenced);
        Assert.Null(node.Payload);
    }

    [Fact]
    public void AddRef_PreventsRelease_PayloadStillAccessible()
    {
        var image = new WorldImage();
        var node = new ChainNode<WorldImage>();
        node.InitializeBase(5);
        node.Payload = image;

        node.AddRef();
        node.Release();

        Assert.False(node.IsUnreferenced);
        Assert.Same(image, node.Payload);
    }

    [Fact]
    public void AddRef_ThenFullRelease_ReachesZero()
    {
        var node = new ChainNode<WorldImage>();
        node.InitializeBase(5);
        node.Payload = new WorldImage();

        node.AddRef();
        node.Release();
        node.Release();

        Assert.True(node.IsUnreferenced);
    }
}
