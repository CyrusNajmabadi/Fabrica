using System.Numerics;
using System.Runtime.CompilerServices;
using Fabrica.Core.Collections;
using Xunit;

namespace Fabrica.Core.Tests.Collections;

public class ProducerConsumerQueueSizingTests
{
    [Fact]
    public void SlabLength_IsPowerOfTwo()
    {
        var length = ProducerConsumerQueue<object>.SlabSizeHelper.SlabLength;
        Assert.True(BitOperations.IsPow2(length), $"SlabLength {length} is not a power of 2.");
    }

    [Fact]
    public void SlabShift_IsLog2OfSlabLength() =>
        Assert.Equal(
            BitOperations.Log2((uint)ProducerConsumerQueue<object>.SlabSizeHelper.SlabLength),
            ProducerConsumerQueue<object>.SlabSizeHelper.SlabShift);

    [Fact]
    public void OffsetMask_IsSlabLengthMinusOne() =>
        Assert.Equal(
            ProducerConsumerQueue<object>.SlabSizeHelper.SlabLength - 1,
            ProducerConsumerQueue<object>.SlabSizeHelper.OffsetMask);

    [Fact]
    public void ArrayStaysUnderLargeObjectHeapThreshold()
    {
        var itemSize = Unsafe.SizeOf<object>();
        var arrayBytes = (ProducerConsumerQueue<object>.SlabSizeHelper.SlabLength * itemSize) + 32;
        Assert.True(arrayBytes < 85_000, $"Array size {arrayBytes} bytes exceeds LOH threshold.");
    }

    [Fact]
    public void NextPowerOfTwo_WouldExceedThreshold()
    {
        var itemSize = Unsafe.SizeOf<object>();
        var doubledLength = ProducerConsumerQueue<object>.SlabSizeHelper.SlabLength * 2;
        var doubledBytes = (doubledLength * itemSize) + 32;
        Assert.True(doubledBytes >= 85_000, $"Doubled array {doubledBytes} bytes still fits — slab is not maximally sized.");
    }

    [Fact]
    public void DifferentItemTypes_ProduceDifferentSlabLengths()
    {
        var smallItem = ProducerConsumerQueue<byte>.SlabSizeHelper.SlabLength;
        var largeItem = ProducerConsumerQueue<LargePayload>.SlabSizeHelper.SlabLength;
        Assert.True(
            smallItem >= largeItem,
            $"Smaller item type should yield equal or larger slab length: small={smallItem}, large={largeItem}.");
    }

    [Fact]
    public void SlabLength_IsAtLeastOne() =>
        Assert.True(ProducerConsumerQueue<LargePayload>.SlabSizeHelper.SlabLength >= 1);

    [Fact]
    public void OversizedItem_ProducesSlabLengthOfOne()
    {
        Assert.Equal(1, ProducerConsumerQueue<OversizedPayload>.SlabSizeHelper.SlabLength);
        Assert.Equal(0, ProducerConsumerQueue<OversizedPayload>.SlabSizeHelper.SlabShift);
        Assert.Equal(0, ProducerConsumerQueue<OversizedPayload>.SlabSizeHelper.OffsetMask);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 4096)]
    private struct LargePayload;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 90_000)]
    private struct OversizedPayload;
}
