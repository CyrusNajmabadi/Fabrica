namespace Fabrica.Core.Collections;

/// <summary>
/// A lightweight view over a contiguous range of <see cref="PipelineEntry{TPayload}"/> values stored across one or more
/// <see cref="Slab{TPayload}"/> arrays. Returned by <see cref="SlabList{TPayload}.ConsumerAcquireEntries"/> and passed back to
/// <see cref="SlabList{TPayload}.ConsumerReleaseEntries"/>.
///
/// The consumer always acquires all available entries and releases the entire range when done. This acquire/release model is
/// intentionally whole-range: the consumer processes everything the producer has published since the last release. If partial
/// consumption were ever needed, the API could generalize to explicit position-based range requests and count-based advancement.
///
/// Declared as <c>ref struct</c> so the indexer can return <c>ref readonly</c> entries without copies. The struct is stack-only
/// and cannot outlive the frame in which it was acquired.
/// </summary>
public readonly ref struct SlabRange<TPayload>
{
    /// <summary>The slab containing the first entry in this range (at offset <see cref="_startOffset"/>).</summary>
    private readonly Slab<TPayload>? _startSlab;

    /// <summary>Offset within <see cref="_startSlab"/> where this range begins.</summary>
    private readonly int _startOffset;

    /// <summary>Total number of entries in this range, potentially spanning multiple slabs.</summary>
    private readonly long _count;

    /// <summary>Number of entries per slab — used by the indexer and enumerator to navigate slab boundaries.</summary>
    private readonly int _slabLength;

    internal SlabRange(Slab<TPayload> startSlab, int startOffset, long count, int slabLength)
    {
        _startSlab = startSlab;
        _startOffset = startOffset;
        _count = count;
        _slabLength = slabLength;
    }

    public long Count => _count;

    public bool IsEmpty => _count == 0;

    public ref readonly PipelineEntry<TPayload> this[long index]
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
    /// Stack-only enumerator for zero-allocation <c>foreach</c> over the range. Tracks the current slab and offset, advancing
    /// through <see cref="Slab{TPayload}.Next"/> when crossing a slab boundary.
    /// </summary>
    public ref struct Enumerator
    {
        private Slab<TPayload>? _currentSlab;
        private int _currentOffset;
        private long _remaining;
        private readonly int _slabLength;

        internal Enumerator(Slab<TPayload>? startSlab, int startOffset, long count, int slabLength)
        {
            _currentSlab = startSlab;
            _currentOffset = startOffset - 1;
            _remaining = count;
            _slabLength = slabLength;
        }

        public readonly ref readonly PipelineEntry<TPayload> Current =>
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
