using System.Numerics;
using System.Runtime.CompilerServices;
using Fabrica.Core.Collections;
using Xunit;

namespace Fabrica.Core.Tests.Collections;

public class SlabSizeHelperTests
{
    [Fact]
    public void SlabLength_IsPowerOfTwo()
    {
        var length = SlabSizeHelper<object>.SlabLength;
        Assert.True(BitOperations.IsPow2(length), $"SlabLength {length} is not a power of 2.");
    }

    [Fact]
    public void SlabShift_IsLog2OfSlabLength() =>
        Assert.Equal(
            BitOperations.Log2((uint)SlabSizeHelper<object>.SlabLength),
            SlabSizeHelper<object>.SlabShift);

    [Fact]
    public void OffsetMask_IsSlabLengthMinusOne() =>
        Assert.Equal(SlabSizeHelper<object>.SlabLength - 1, SlabSizeHelper<object>.OffsetMask);

    [Fact]
    public void ArrayStaysUnderLargeObjectHeapThreshold()
    {
        var entrySize = Unsafe.SizeOf<PipelineEntry<object>>();
        var arrayBytes = (SlabSizeHelper<object>.SlabLength * entrySize) + 32;
        Assert.True(arrayBytes < 85_000, $"Array size {arrayBytes} bytes exceeds LOH threshold.");
    }

    [Fact]
    public void NextPowerOfTwo_WouldExceedThreshold()
    {
        var entrySize = Unsafe.SizeOf<PipelineEntry<object>>();
        var doubledLength = SlabSizeHelper<object>.SlabLength * 2;
        var doubledBytes = (doubledLength * entrySize) + 32;
        Assert.True(doubledBytes >= 85_000, $"Doubled array {doubledBytes} bytes still fits — slab is not maximally sized.");
    }

    [Fact]
    public void DifferentPayloadTypes_ProduceDifferentSlabLengths()
    {
        var smallPayload = SlabSizeHelper<byte>.SlabLength;
        var largePayload = SlabSizeHelper<LargePayload>.SlabLength;
        Assert.True(
            smallPayload >= largePayload,
            $"Smaller payload type should yield equal or larger slab length: small={smallPayload}, large={largePayload}.");
    }

    [Fact]
    public void SlabLength_IsAtLeastOne() =>
        Assert.True(SlabSizeHelper<LargePayload>.SlabLength >= 1);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 4096)]
    private struct LargePayload;

}
