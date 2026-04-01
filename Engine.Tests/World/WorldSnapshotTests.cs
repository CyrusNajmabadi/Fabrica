using Engine.Pipeline;
using Engine.Tests.Helpers;
using Engine.World;
using Xunit;

namespace Engine.Tests.World;

/// <summary>
/// Tests for <see cref="BaseProductionLoop{WorldImage}.ChainNode"/> payload lifecycle — setting, clearing, and ref-count
/// interaction with payload visibility.
/// </summary>
public sealed class WorldSnapshotTests
{
    private readonly TestChainHarness _harness = new();
    private BaseProductionLoop<WorldImage>.ChainTestAccessor Accessor => _harness.GetChainTestAccessor();

    [Fact]
    public void InitializeBase_AndPayload_SetsCorrectly()
    {
        var image = new WorldImage();
        var node = this.Accessor.CreateNode(5);
        this.Accessor.SetPayload(node, image);

        Assert.Same(image, node.Payload);
        Assert.Equal(5, node.SequenceNumber);
    }

    [Fact]
    public void Release_ToZero_AllowsPayloadClearing()
    {
        var image = new WorldImage();
        var node = this.Accessor.CreateNode(10);
        this.Accessor.SetPayload(node, image);

        this.Accessor.ClearPayload(node);
        this.Accessor.Release(node);

        Assert.True(node.IsUnreferenced);
        Assert.Null(node.Payload);
    }

    [Fact]
    public void AddRef_PreventsRelease_PayloadStillAccessible()
    {
        var image = new WorldImage();
        var node = this.Accessor.CreateNode(5);
        this.Accessor.SetPayload(node, image);

        this.Accessor.AddRef(node);
        this.Accessor.Release(node);

        Assert.False(node.IsUnreferenced);
        Assert.Same(image, node.Payload);
    }

    [Fact]
    public void AddRef_ThenFullRelease_ReachesZero()
    {
        var node = this.Accessor.CreateNode(5);
        this.Accessor.SetPayload(node, new WorldImage());

        this.Accessor.AddRef(node);
        this.Accessor.Release(node);
        this.Accessor.Release(node);

        Assert.True(node.IsUnreferenced);
    }
}
