namespace Fabrica.Core.Collections;

public sealed partial class ProducerConsumerQueue<T>
{
    /// <summary>
    /// A lightweight view over a contiguous range of items stored across one or more slabs. Returned by
    /// <see cref="ConsumerAcquire"/>.
    ///
    /// The consumer acquires all available items, processes them, then calls <see cref="ConsumerAdvance"/> with the number of
    /// items to release. Typically the consumer advances by <c>Count - 1</c>, holding back the last entry as the "previous"
    /// for the next frame's interpolation range.
    ///
    /// Declared as <c>ref struct</c> so the indexer can return <c>ref readonly</c> items without copies. The struct is stack-only
    /// and cannot outlive the frame in which it was acquired.
    /// </summary>
    public readonly ref struct Segment
    {
        /// <summary>First slab in the range. Null only for a default (empty) segment.</summary>
        private readonly Slab? _startSlab;

        /// <summary>Index within <see cref="_startSlab"/> where the first item lives.</summary>
        private readonly int _startOffset;

        /// <summary>Total number of items in this segment.</summary>
        private readonly long _count;

        /// <summary>Number of items per slab — used to compute slab boundaries when indexing across multiple slabs.</summary>
        private readonly int _slabLength;

        /// <summary>Absolute queue position of <c>this[0]</c>. Used by the consumption loop to compute positions for pinning.</summary>
        private readonly long _startPosition;

        internal Segment(Slab startSlab, int startOffset, long count, int slabLength, long startPosition)
        {
            _startSlab = startSlab;
            _startOffset = startOffset;
            _count = count;
            _slabLength = slabLength;
            _startPosition = startPosition;
        }

        public long Count => _count;

        public bool IsEmpty => _count == 0;

        /// <summary>The absolute queue position of the first item in this segment.</summary>
        public long StartPosition => _startPosition;

        public ref readonly T this[long index]
        {
            get
            {
                if ((ulong)index >= (ulong)_count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                var localPosition = _startOffset + index;
                var slab = _startSlab!;

                while (localPosition >= _slabLength)
                {
                    slab = slab.Next!;
                    localPosition -= _slabLength;
                }

                return ref slab.Entries[(int)localPosition];
            }
        }

        public ref readonly T this[Index index] => ref this[(long)index.GetOffset((int)_count)];

        public Enumerator GetEnumerator() => new(_startSlab, _startOffset, _count, _slabLength);

        /// <summary>
        /// Stack-only enumerator for zero-allocation <c>foreach</c> over the segment. Tracks the current slab and offset, advancing
        /// through slab links when crossing a slab boundary.
        /// </summary>
        public ref struct Enumerator
        {
            private Slab? _currentSlab;
            private int _currentOffset;
            private long _remaining;
            private readonly int _slabLength;

            internal Enumerator(Slab? startSlab, int startOffset, long count, int slabLength)
            {
                _currentSlab = startSlab;
                _currentOffset = startOffset - 1;
                _remaining = count;
                _slabLength = slabLength;
            }

            public readonly ref readonly T Current =>
                ref _currentSlab!.Entries[_currentOffset];

            public bool MoveNext()
            {
                if (_remaining <= 0)
                    return false;

                _remaining--;
                _currentOffset++;

                if (_currentOffset >= _slabLength)
                {
                    _currentSlab = _currentSlab!.Next;
                    _currentOffset = 0;
                }

                return true;
            }
        }
    }
}
