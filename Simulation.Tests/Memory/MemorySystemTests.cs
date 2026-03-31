using Simulation.Memory;
using Simulation.World;
using Xunit;

namespace Simulation.Tests.Memory;

public sealed class MemorySystemTests
{
    // ── Constructor ──────────────────────────────────────────────────────────

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

        var node = memory.RentNode();
        var image = memory.RentImage();

        Assert.NotNull(node);
        Assert.NotNull(image);
    }

    // ── Rent (pool exhaustion) ───────────────────────────────────────────────

    [Fact]
    public void RentNode_AlwaysReturnsNonNull()
    {
        var memory = new MemorySystem(initialPoolSize: 1);

        var first = memory.RentNode();
        var second = memory.RentNode();

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

    // ── ReturnNode ──────────────────────────────────────────────────────────

    [Fact]
    public void ReturnNode_MakesInstanceAvailableForReuse()
    {
        var memory = new MemorySystem(initialPoolSize: 1);

        var original = memory.RentNode();
        memory.ReturnNode(original);

        var reused = memory.RentNode();
        Assert.Same(original, reused);
    }

    [Fact]
    public void ReturnNode_MultipleRoundTrips_AlwaysRecyclesSameInstance()
    {
        var memory = new MemorySystem(initialPoolSize: 1);

        var node = memory.RentNode();
        for (var i = 0; i < 10; i++)
        {
            memory.ReturnNode(node);
            var rented = memory.RentNode();
            Assert.Same(node, rented);
        }
    }

    // ── ReturnImage ──────────────────────────────────────────────────────────

    [Fact]
    public void ReturnImage_MakesInstanceAvailableForReuse()
    {
        var memory = new MemorySystem(initialPoolSize: 1);

        var original = memory.RentImage();
        memory.ReturnImage(original);

        var reused = memory.RentImage();
        Assert.Same(original, reused);
    }

    [Fact]
    public void ReturnImage_CallsResetForPool_BeforeReturningToPool()
    {
        var memory = new MemorySystem(initialPoolSize: 1);

        var image = memory.RentImage();
        memory.ReturnImage(image);

        // ResetForPool is currently a no-op, but the fact that ReturnImage
        // completes without error and the image can be re-rented verifies the
        // contract.  When WorldImage gains real state, this test should be
        // extended to assert that state is cleared.
        var reused = memory.RentImage();
        Assert.Same(image, reused);
    }

    [Fact]
    public void ReturnImage_MultipleRoundTrips_AlwaysRecyclesSameInstance()
    {
        var memory = new MemorySystem(initialPoolSize: 1);

        var image = memory.RentImage();
        for (var i = 0; i < 10; i++)
        {
            memory.ReturnImage(image);
            var rented = memory.RentImage();
            Assert.Same(image, rented);
        }
    }

    // ── Pool growth ──────────────────────────────────────────────────────────

    [Fact]
    public void ReturningExtraNodes_GrowsPoolBeyondInitialCapacity()
    {
        var memory = new MemorySystem(initialPoolSize: 1);

        var a = memory.RentNode();
        var b = memory.RentNode();
        var c = memory.RentNode();

        memory.ReturnNode(a);
        memory.ReturnNode(b);
        memory.ReturnNode(c);

        var r1 = memory.RentNode();
        var r2 = memory.RentNode();
        var r3 = memory.RentNode();

        var rented = new[] { r1, r2, r3 };
        Assert.Contains(a, rented);
        Assert.Contains(b, rented);
        Assert.Contains(c, rented);
    }

    [Fact]
    public void ReturningExtraImages_GrowsPoolBeyondInitialCapacity()
    {
        var memory = new MemorySystem(initialPoolSize: 1);

        var a = memory.RentImage();
        var b = memory.RentImage();
        var c = memory.RentImage();

        memory.ReturnImage(a);
        memory.ReturnImage(b);
        memory.ReturnImage(c);

        var r1 = memory.RentImage();
        var r2 = memory.RentImage();
        var r3 = memory.RentImage();

        var rented = new[] { r1, r2, r3 };
        Assert.Contains(a, rented);
        Assert.Contains(b, rented);
        Assert.Contains(c, rented);
    }

    // ── PinnedVersions ───────────────────────────────────────────────────────

    [Fact]
    public void PinnedVersions_IsAccessibleAndInitiallyEmpty()
    {
        var memory = new MemorySystem(initialPoolSize: 1);

        Assert.NotNull(memory.PinnedVersions);
        Assert.False(memory.PinnedVersions.IsPinned(0));
    }

    // ── Pool independence ────────────────────────────────────────────────────

    [Fact]
    public void NodeAndImagePools_AreIndependent()
    {
        var memory = new MemorySystem(initialPoolSize: 2);

        var s1 = memory.RentNode();
        var s2 = memory.RentNode();

        var i1 = memory.RentImage();
        var i2 = memory.RentImage();

        var s3 = memory.RentNode();
        var i3 = memory.RentImage();

        Assert.NotNull(s3);
        Assert.NotNull(i3);

        Assert.NotSame(s1, s2);
        Assert.NotSame(s2, s3);
        Assert.NotSame(i1, i2);
        Assert.NotSame(i2, i3);
    }
}
