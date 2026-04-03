using Fabrica.Core.Memory;

namespace Fabrica.Core.Collections;

public sealed partial class ProducerConsumerQueue<T>
{
    /// <summary>
    /// Delegates to the shared <see cref="SlabSizeHelper{T}"/> for LOH-aware power-of-2 slab sizing. Kept as a nested
    /// type alias so existing references within <see cref="ProducerConsumerQueue{T}"/> continue to compile unchanged.
    /// </summary>
    internal static class SlabSizeHelper
    {
        public static int SlabLength => SlabSizeHelper<T>.SlabLength;
        public static int SlabShift => SlabSizeHelper<T>.SlabShift;
        public static int OffsetMask => SlabSizeHelper<T>.OffsetMask;
    }
}
