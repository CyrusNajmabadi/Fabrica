using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class ArenaIndexTests
{
    // ═══════════════════════════ NoChild sentinel ═════════════════════════

    [Fact]
    public void NoChild_IsMinusOne()
        => Assert.Equal(-1, ArenaIndex.NoChild);

    [Fact]
    public void NoChild_IsNotLocal()
        => Assert.False(ArenaIndex.IsLocal(ArenaIndex.NoChild));

    [Fact]
    public void NoChild_IsNotGlobal()
        => Assert.False(ArenaIndex.IsGlobal(ArenaIndex.NoChild));

    // ═══════════════════════════ Global indices ══════════════════════════

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    public void Global_IsGlobal(int value)
        => Assert.True(ArenaIndex.IsGlobal(value));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Global_IsNotLocal(int value)
        => Assert.False(ArenaIndex.IsLocal(value));

    // ═══════════════════════════ Tag / untag roundtrip ═══════════════════

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(1000)]
    [InlineData(0x7FFF_FFFE)]
    public void TagLocal_ThenUntagLocal_Roundtrips(int localIndex)
    {
        var tagged = ArenaIndex.TagLocal(localIndex);
        Assert.True(ArenaIndex.IsLocal(tagged));
        Assert.False(ArenaIndex.IsGlobal(tagged));
        Assert.Equal(localIndex, ArenaIndex.UntagLocal(tagged));
    }

    [Fact]
    public void TagLocal_Zero_ProducesIntMinValue()
    {
        var tagged = ArenaIndex.TagLocal(0);
        Assert.Equal(int.MinValue, tagged);
        Assert.True(ArenaIndex.IsLocal(tagged));
    }

    [Fact]
    public void TagLocal_MaxValid_ProducesMinusTwo()
    {
        var tagged = ArenaIndex.TagLocal(0x7FFF_FFFE);
        Assert.Equal(-2, tagged);
        Assert.True(ArenaIndex.IsLocal(tagged));
    }

    // ═══════════════════════════ Tagged values are distinct from NoChild ═

    [Fact]
    public void TaggedValues_NeverEqualNoChild()
    {
        for (var i = 0; i < 1000; i++)
        {
            var tagged = ArenaIndex.TagLocal(i);
            Assert.NotEqual(ArenaIndex.NoChild, tagged);
        }
    }

    // ═══════════════════════════ Classification of all zones ═════════════

    [Fact]
    public void Classification_MutuallyExclusive()
    {
        var testValues = new[] { -2, int.MinValue, ArenaIndex.NoChild, 0, 1, int.MaxValue };

        foreach (var v in testValues)
        {
            var isLocal = ArenaIndex.IsLocal(v);
            var isGlobal = ArenaIndex.IsGlobal(v);
            var isNoChild = v == ArenaIndex.NoChild;

            var trueCount = (isLocal ? 1 : 0) + (isGlobal ? 1 : 0) + (isNoChild ? 1 : 0);
            Assert.Equal(1, trueCount);
        }
    }
}
