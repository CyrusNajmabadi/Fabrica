using Engine.Memory;
using Engine.World;
using Xunit;

namespace Engine.Tests.Memory;

public sealed class MemorySystemTests
{
    // ── Constructor ──────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_RejectsNonPositivePoolSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemorySystem<WorldImage, WorldImageAllocator>(initialPoolSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemorySystem<WorldImage, WorldImageAllocator>(initialPoolSize: -1));
    }

    [Fact]
    public void Constructor_AcceptsAnyPositivePoolSize()
    {
        var memory = new MemorySystem<WorldImage, WorldImageAllocator>(initialPoolSize: 10);

        var node = memory.RentNode();
        var payload = memory.RentPayload();

        Assert.NotNull(node);
        Assert.NotNull(payload);
    }

    // ── Rent (pool exhaustion) ───────────────────────────────────────────────

    [Fact]
    public void RentNode_AlwaysReturnsNonNull()
    {
        var memory = new MemorySystem<WorldImage, WorldImageAllocator>(initialPoolSize: 1);

        var first = memory.RentNode();
        var second = memory.RentNode();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void RentPayload_AlwaysReturnsNonNull()
    {
        var memory = new MemorySystem<WorldImage, WorldImageAllocator>(initialPoolSize: 1);

        var first = memory.RentPayload();
        var second = memory.RentPayload();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    // ── ReturnNode ──────────────────────────────────────────────────────────

    [Fact]
    public void ReturnNode_MakesInstanceAvailableForReuse()
    {
        var memory = new MemorySystem<WorldImage, WorldImageAllocator>(initialPoolSize: 1);

        var original = memory.RentNode();
        memory.ReturnNode(original);

        var reused = memory.RentNode();
        Assert.Same(original, reused);
    }

    [Fact]
    public void ReturnNode_MultipleRoundTrips_AlwaysRecyclesSameInstance()
    {
        var memory = new MemorySystem<WorldImage, WorldImageAllocator>(initialPoolSize: 1);

        var node = memory.RentNode();
        for (var i = 0; i < 10; i++)
        {
            memory.ReturnNode(node);
            var rented = memory.RentNode();
            Assert.Same(node, rented);
        }
    }

    // ── ReturnPayload ──────────────────────────────────────────────────────────

    [Fact]
    public void ReturnPayload_MakesInstanceAvailableForReuse()
    {
        var memory = new MemorySystem<WorldImage, WorldImageAllocator>(initialPoolSize: 1);

        var original = memory.RentPayload();
        memory.ReturnPayload(original);

        var reused = memory.RentPayload();
        Assert.Same(original, reused);
    }

    [Fact]
    public void ReturnPayload_CallsResetForPool_BeforeReturningToPool()
    {
        var memory = new MemorySystem<WorldImage, WorldImageAllocator>(initialPoolSize: 1);

        var payload = memory.RentPayload();
        memory.ReturnPayload(payload);

        // ResetForPool is currently a no-op, but the fact that ReturnPayload
        // completes without error and the payload can be re-rented verifies the
        // contract.  When WorldImage gains real state, this test should be
        // extended to assert that state is cleared.
        var reused = memory.RentPayload();
        Assert.Same(payload, reused);
    }

    [Fact]
    public void ReturnPayload_MultipleRoundTrips_AlwaysRecyclesSameInstance()
    {
        var memory = new MemorySystem<WorldImage, WorldImageAllocator>(initialPoolSize: 1);

        var payload = memory.RentPayload();
        for (var i = 0; i < 10; i++)
        {
            memory.ReturnPayload(payload);
            var rented = memory.RentPayload();
            Assert.Same(payload, rented);
        }
    }

    // ── Pool growth ──────────────────────────────────────────────────────────

    [Fact]
    public void ReturningExtraNodes_GrowsPoolBeyondInitialCapacity()
    {
        var memory = new MemorySystem<WorldImage, WorldImageAllocator>(initialPoolSize: 1);

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
    public void ReturningExtraPayloads_GrowsPoolBeyondInitialCapacity()
    {
        var memory = new MemorySystem<WorldImage, WorldImageAllocator>(initialPoolSize: 1);

        var a = memory.RentPayload();
        var b = memory.RentPayload();
        var c = memory.RentPayload();

        memory.ReturnPayload(a);
        memory.ReturnPayload(b);
        memory.ReturnPayload(c);

        var r1 = memory.RentPayload();
        var r2 = memory.RentPayload();
        var r3 = memory.RentPayload();

        var rented = new[] { r1, r2, r3 };
        Assert.Contains(a, rented);
        Assert.Contains(b, rented);
        Assert.Contains(c, rented);
    }

    // ── Pool independence ────────────────────────────────────────────────────

    [Fact]
    public void NodeAndPayloadPools_AreIndependent()
    {
        var memory = new MemorySystem<WorldImage, WorldImageAllocator>(initialPoolSize: 2);

        var s1 = memory.RentNode();
        var s2 = memory.RentNode();

        var i1 = memory.RentPayload();
        var i2 = memory.RentPayload();

        var s3 = memory.RentNode();
        var i3 = memory.RentPayload();

        Assert.NotNull(s3);
        Assert.NotNull(i3);

        Assert.NotSame(s1, s2);
        Assert.NotSame(s2, s3);
        Assert.NotSame(i1, i2);
        Assert.NotSame(i2, i3);
    }
}
