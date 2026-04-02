using System.Numerics;
using System.Runtime.CompilerServices;

namespace Fabrica.Core.Collections;

public sealed partial class ProducerConsumerQueue<T>
{
    /// <summary>
    /// Computes the optimal slab array length. Mirrors Roslyn's <c>SegmentedArrayHelper</c>: finds the largest power-of-2 element
    /// count whose backing array stays below the LOH threshold (~85,000 bytes). Power-of-2 sizing enables bit-shift division and
    /// bitwise-AND modulo for O(1) position-to-offset mapping.
    /// </summary>
    internal static class SlabSizeHelper
    {
        private const int LargeObjectHeapThreshold = 85_000;
        private const int ArrayBaseOverhead = 32;

        /// <summary>Number of items per slab — always a power of 2.</summary>
        public static readonly int SlabLength;

        /// <summary><c>log2(SlabLength)</c> — for bit-shift indexing.</summary>
        public static readonly int SlabShift;

        /// <summary><c>SlabLength - 1</c> — for bitwise-AND offset masking.</summary>
        public static readonly int OffsetMask;

        static SlabSizeHelper()
        {
            var itemSize = Unsafe.SizeOf<T>();
            var maxElements = Math.Max((LargeObjectHeapThreshold - ArrayBaseOverhead) / itemSize, 1);

            SlabLength = (int)BitOperations.RoundUpToPowerOf2((uint)(maxElements + 1)) >> 1;
            if (SlabLength == 0)
                SlabLength = 1;

            SlabShift = BitOperations.Log2((uint)SlabLength);
            OffsetMask = SlabLength - 1;
        }
    }
}
