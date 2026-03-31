using Engine.Memory;
using Xunit;

namespace Engine.Tests.Memory;

public sealed class PinnedVersionsTests
{
    [Fact]
    public void Pin_ThenUnpin_TracksMembership()
    {
        var pinned = new PinnedVersions();
        object owner = new();

        Assert.False(pinned.IsPinned(42));

        pinned.Pin(42, owner);
        Assert.True(pinned.IsPinned(42));

        pinned.Unpin(42, owner);
        Assert.False(pinned.IsPinned(42));
    }

    [Fact]
    public void DifferentOwners_MustAllUnpinBeforeTickIsReleased()
    {
        var pinned = new PinnedVersions();
        object firstOwner = new();
        object secondOwner = new();

        pinned.Pin(7, firstOwner);
        pinned.Pin(7, secondOwner);
        Assert.True(pinned.IsPinned(7));

        pinned.Unpin(7, firstOwner);
        Assert.True(pinned.IsPinned(7));

        pinned.Unpin(7, secondOwner);
        Assert.False(pinned.IsPinned(7));
    }

    [Fact]
    public void Pin_SameOwnerTwice_Throws()
    {
        var pinned = new PinnedVersions();
        object owner = new();

        pinned.Pin(7, owner);

        Assert.Throws<InvalidOperationException>(() => pinned.Pin(7, owner));
    }

    [Fact]
    public void Unpin_WithoutMatchingPin_Throws()
    {
        var pinned = new PinnedVersions();
        object owner = new();

        Assert.Throws<InvalidOperationException>(() => pinned.Unpin(7, owner));
    }

    [Fact]
    public void Unpin_WithDifferentOwnerThanWasPinned_Throws()
    {
        var pinned = new PinnedVersions();
        object pinnedOwner = new();
        object differentOwner = new();

        pinned.Pin(7, pinnedOwner);

        Assert.Throws<InvalidOperationException>(() => pinned.Unpin(7, differentOwner));
        Assert.True(pinned.IsPinned(7));
    }
}
