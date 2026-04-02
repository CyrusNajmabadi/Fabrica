namespace Fabrica.Core.Collections;

public sealed partial class ProducerConsumerQueue<T>
{
    /// <summary>
    /// A lightweight view over a contiguous range of items stored across one or more slabs. Returned by
    /// <see cref="ConsumerAcquire"/> and passed back to <see cref="ConsumerRelease"/>.
    ///
    /// The consumer always acquires all available items and releases the entire segment when done. This acquire/release model is
    /// intentionally whole-range: the consumer processes everything the producer has published since the last release.
    ///
    /// Declared as <c>ref struct</c> so the indexer can return <c>ref readonly</c> items without copies. The struct is stack-only
    /// and cannot outlive the frame in which it was acquired.
    /// </summary>
    public readonly ref struct Segment
    {
        private readonly Slab? _startSlab;
        private readonly int _startOffset;
        private readonly long _count;
        private readonly int _slabLength;

        internal Segment(Slab startSlab, int startOffset, long count, int slabLength)
        {
            _startSlab = startSlab;
            _startOffset = startOffset;
            _count = count;
            _slabLength = slabLength;
        }

        public long Count => _count;

        public bool IsEmpty => _count == 0;

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
