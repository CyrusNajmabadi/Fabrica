using System.Numerics;
using System.Runtime.CompilerServices;
using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Collections;

public class ProducerConsumerQueueSizingTests
{
    [Fact]
    public void SlabLength_IsPowerOfTwo()
    {
        var length = SlabSizeHelper<object>.SlabLength;
        Assert.True(BitOperations.IsPow2(length), $"SlabLength {length} is not a power of 2.");
    }

    [Fact]
    public void SlabShift_IsLog2OfSlabLength()
        => Assert.Equal(
            BitOperations.Log2((uint)SlabSizeHelper<object>.SlabLength),
            SlabSizeHelper<object>.SlabShift);

    [Fact]
    public void OffsetMask_IsSlabLengthMinusOne()
        => Assert.Equal(
            SlabSizeHelper<object>.SlabLength - 1,
            SlabSizeHelper<object>.OffsetMask);

    [Fact]
    public void ArrayStaysUnderLargeObjectHeapThreshold()
    {
        var itemSize = Unsafe.SizeOf<object>();
        var arrayBytes = (SlabSizeHelper<object>.SlabLength * itemSize) + 32;
        Assert.True(arrayBytes < 85_000, $"Array size {arrayBytes} bytes exceeds LOH threshold.");
    }

    [Fact]
    public void NextPowerOfTwo_WouldExceedThreshold()
    {
        var itemSize = Unsafe.SizeOf<object>();
        var doubledLength = SlabSizeHelper<object>.SlabLength * 2;
        var doubledBytes = (doubledLength * itemSize) + 32;
        Assert.True(doubledBytes >= 85_000, $"Doubled array {doubledBytes} bytes still fits — slab is not maximally sized.");
    }

    [Fact]
    public void DifferentItemTypes_ProduceDifferentSlabLengths()
    {
        var smallItem = SlabSizeHelper<byte>.SlabLength;
        var largeItem = SlabSizeHelper<LargePayload>.SlabLength;
        Assert.True(
            smallItem >= largeItem,
            $"Smaller item type should yield equal or larger slab length: small={smallItem}, large={largeItem}.");
    }

    [Fact]
    public void SlabLength_IsAtLeastOne()
        => Assert.True(SlabSizeHelper<LargePayload>.SlabLength >= 1);

    [Fact]
    public void OversizedItem_ProducesSlabLengthOfOne()
    {
        Assert.Equal(1, SlabSizeHelper<OversizedPayload>.SlabLength);
        Assert.Equal(0, SlabSizeHelper<OversizedPayload>.SlabShift);
        Assert.Equal(0, SlabSizeHelper<OversizedPayload>.OffsetMask);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 4096)]
    private struct LargePayload;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 90_000)]
    private struct OversizedPayload;
}
