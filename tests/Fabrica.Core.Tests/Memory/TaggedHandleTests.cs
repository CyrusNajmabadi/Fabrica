using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class TaggedHandleTests
{
    // ═══════════════════════════ Classification ═══════════════════════════

    [Fact]
    public void None_IsNone()
    {
        Assert.True(TaggedHandle.IsNone(-1));
        Assert.False(TaggedHandle.IsGlobal(-1));
        Assert.False(TaggedHandle.IsLocal(-1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(int.MaxValue)]
    public void Global_IsGlobal(int index)
    {
        Assert.True(TaggedHandle.IsGlobal(index));
        Assert.False(TaggedHandle.IsLocal(index));
        Assert.False(TaggedHandle.IsNone(index));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(127, 0)]
    [InlineData(64, 0x00FF_FFFF)]
    public void Local_IsLocal(int threadId, int localIndex)
    {
        var encoded = TaggedHandle.EncodeLocal(threadId, localIndex);
        Assert.True(TaggedHandle.IsLocal(encoded));
        Assert.False(TaggedHandle.IsGlobal(encoded));
        Assert.False(TaggedHandle.IsNone(encoded));
    }

    // ═══════════════════════════ Roundtrip ════════════════════════════════

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(127, 0)]
    [InlineData(0, 0x00FF_FFFF)]
    [InlineData(127, 0x00FF_FFFF)]
    [InlineData(64, 12345)]
    [InlineData(1, 1)]
    public void EncodeLocal_Roundtrips(int threadId, int localIndex)
    {
        var encoded = TaggedHandle.EncodeLocal(threadId, localIndex);
        Assert.Equal(threadId, TaggedHandle.DecodeThreadId(encoded));
        Assert.Equal(localIndex, TaggedHandle.DecodeLocalIndex(encoded));
    }

    // ═══════════════════════════ Boundary values ═════════════════════════

    [Fact]
    public void MaxThreadId_Roundtrips()
    {
        var encoded = TaggedHandle.EncodeLocal(127, 0);
        Assert.Equal(127, TaggedHandle.DecodeThreadId(encoded));
        Assert.Equal(0, TaggedHandle.DecodeLocalIndex(encoded));
    }

    [Fact]
    public void MaxLocalIndex_Roundtrips()
    {
        var encoded = TaggedHandle.EncodeLocal(0, TaggedHandle.MaxLocalIndex);
        Assert.Equal(0, TaggedHandle.DecodeThreadId(encoded));
        Assert.Equal(TaggedHandle.MaxLocalIndex, TaggedHandle.DecodeLocalIndex(encoded));
    }

    [Fact]
    public void MaxBoth_Roundtrips()
    {
        var encoded = TaggedHandle.EncodeLocal(127, TaggedHandle.MaxLocalIndex);
        Assert.Equal(127, TaggedHandle.DecodeThreadId(encoded));
        Assert.Equal(TaggedHandle.MaxLocalIndex, TaggedHandle.DecodeLocalIndex(encoded));
    }

    // ═══════════════════════════ Local != None ════════════════════════════

    [Fact]
    public void AllLocalEncodings_AreNotNone()
    {
        for (var threadId = 0; threadId < TaggedHandle.MaxThreads; threadId++)
        {
            var encoded = TaggedHandle.EncodeLocal(threadId, 0);
            Assert.False(TaggedHandle.IsNone(encoded),
                $"EncodeLocal({threadId}, 0) produced -1 (None sentinel).");
        }
    }

    // ═══════════════════════════ Distinctness ═════════════════════════════

    [Fact]
    public void DifferentThreadIds_ProduceDifferentEncodings()
    {
        var a = TaggedHandle.EncodeLocal(0, 100);
        var b = TaggedHandle.EncodeLocal(1, 100);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DifferentLocalIndices_ProduceDifferentEncodings()
    {
        var a = TaggedHandle.EncodeLocal(5, 0);
        var b = TaggedHandle.EncodeLocal(5, 1);
        Assert.NotEqual(a, b);
    }

    // ═══════════════════════════ Constants ════════════════════════════════

    [Fact]
    public void MaxThreads_Is128()
        => Assert.Equal(128, TaggedHandle.MaxThreads);

    [Fact]
    public void MaxLocalIndex_Is16M_Minus1()
        => Assert.Equal(0x00FF_FFFF, TaggedHandle.MaxLocalIndex);
}
