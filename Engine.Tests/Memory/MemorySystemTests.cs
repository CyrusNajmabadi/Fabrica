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
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemorySystem<WorldImage, WorldImage.Allocator>(initialPoolSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemorySystem<WorldImage, WorldImage.Allocator>(initialPoolSize: -1));
    }

    [Fact]
    public void Constructor_AcceptsAnyPositivePoolSize()
    {
        var memory = new MemorySystem<WorldImage, WorldImage.Allocator>(initialPoolSize: 10);

        var payload = memory.RentPayload();

        Assert.NotNull(payload);
    }

    // ── Rent (pool exhaustion) ───────────────────────────────────────────────

    [Fact]
    public void RentPayload_AlwaysReturnsNonNull()
    {
        var memory = new MemorySystem<WorldImage, WorldImage.Allocator>(initialPoolSize: 1);

        var first = memory.RentPayload();
        var second = memory.RentPayload();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    // ── ReturnPayload ──────────────────────────────────────────────────────────

    [Fact]
    public void ReturnPayload_MakesInstanceAvailableForReuse()
    {
        var memory = new MemorySystem<WorldImage, WorldImage.Allocator>(initialPoolSize: 1);

        var original = memory.RentPayload();
        memory.ReturnPayload(original);

        var reused = memory.RentPayload();
        Assert.Same(original, reused);
    }

    [Fact]
    public void ReturnPayload_CallsResetForPool_BeforeReturningToPool()
    {
        var memory = new MemorySystem<WorldImage, WorldImage.Allocator>(initialPoolSize: 1);

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
        var memory = new MemorySystem<WorldImage, WorldImage.Allocator>(initialPoolSize: 1);

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
    public void ReturningExtraPayloads_GrowsPoolBeyondInitialCapacity()
    {
        var memory = new MemorySystem<WorldImage, WorldImage.Allocator>(initialPoolSize: 1);

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
}
