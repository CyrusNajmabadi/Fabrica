using Simulation.Memory;
using Xunit;

namespace Simulation.Tests.Memory;

public sealed class PinnedVersionsTests
{
    [Fact]
    public void Pin_ThenUnpin_TracksMembership()
    {
        var pinned = new PinnedVersions();

        Assert.False(pinned.IsPinned(42));

        pinned.Pin(42);
        Assert.True(pinned.IsPinned(42));

        pinned.Unpin(42);
        Assert.False(pinned.IsPinned(42));
    }

    [Fact]
    public void RepeatedPinAndUnpin_AreHarmless()
    {
        var pinned = new PinnedVersions();

        pinned.Pin(7);
        pinned.Pin(7);
        Assert.True(pinned.IsPinned(7));

        pinned.Unpin(7);
        pinned.Unpin(7);
        Assert.False(pinned.IsPinned(7));
    }
}
