using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Fabrica.Core.Collections;

/// <summary>
/// Lock-free single-producer / single-consumer (SPSC) append-only queue backed by a linked chain of contiguous array segments
/// ("slabs"). Designed for state propagation between the production and consumption threads.
///
/// DESIGN
///   The producer appends items to the tail slab and publishes each one immediately via a volatile write of the producer position.
///   The consumer acquires all items published since its last release and processes them as a contiguous <see cref="Segment"/>.
///   This acquire/release model is intentionally whole-range — the consumer always processes everything available.
///
///   Publish-per-append was chosen over batch publishing to minimize rendering latency: when the production loop processes multiple
///   accumulated ticks in one iteration, the consumer can see each tick as soon as it is appended rather than waiting for the
///   entire batch. The cost is one volatile write per append, which on x86 is just a compiler fence and on ARM is a single
///   <c>dmb</c> barrier — negligible at typical tick rates.
///
/// MEMORY MODEL
///   Two <c>long</c> fields serve as the SPSC synchronization points:
///
///   <c>_producerPosition</c>: Volatile.Write by producer (release fence), Volatile.Read by consumer (acquire fence). The release
///   fence ensures all item writes and slab <c>Next</c> links are visible before the consumer reads the updated position.
///
///   <c>_consumerPosition</c>: Volatile.Write by consumer (release fence), Volatile.Read by producer (acquire fence). The producer
///   reads this during cleanup to determine which items are safe to reclaim.
///
///   Both fields have at most one writer, so no CAS or interlocked operations are needed. Staleness in either direction is
///   conservative: a stale producer position means the consumer sees fewer items (processes them next frame); a stale consumer
///   position means the producer retains items longer (delays cleanup, never frees prematurely).
///
/// SLAB LIFECYCLE
///   Slabs are allocated from a single-threaded free stack on the producer thread. When the producer's cleanup pass finishes
///   clearing all items in a slab, the slab is pushed onto the free stack for reuse. The consumer holds a cached
///   <c>_consumerSlab</c> pointer that is always at or ahead of the cleanup frontier, so a recycled slab is never read by the
///   consumer.
///
/// CACHE-LINE PADDING
///   The two volatile position fields could benefit from cache-line padding to avoid false sharing. This is omitted for now and
///   can be added in a follow-up if profiling shows contention.
/// </summary>
public sealed class ProducerConsumerQueue<T>
{
    // ═══════════════════════════ NESTED TYPES ═════════════════════════════════

    /// <summary>
    /// Callback for <see cref="ProducerCleanup{THandler}"/>. Called once for each item being reclaimed. The handler should inspect
    /// the item and take appropriate action — typically checking whether it is pinned and either copying it to a side table for
    /// deferred processing, or releasing its resources.
    ///
    /// After the handler returns, the slab slot is cleared (<c>= default</c>) regardless of what the handler did. If the item must
    /// be preserved, the handler must copy it before returning.
    ///
    /// Constrained to struct for zero interface-dispatch overhead — the JIT specializes each call through the generic constraint.
    /// </summary>
    public interface ICleanupHandler
    {
        void HandleCleanup(long position, in T item);
    }

    /// <summary>
    /// A contiguous array segment within the queue. Slabs form a singly-linked chain owned by the producer thread. Each slab's
    /// array is sized to stay below the Large Object Heap threshold, giving cache-friendly sequential access without GC promotion
    /// pressure.
    /// </summary>
    internal sealed class Slab(int length)
    {
        /// <summary>Fixed-size array of items. Sized at construction to the slab length.</summary>
        public readonly T[] Entries = new T[length];

        /// <summary>Forward link to the next slab in the chain. Set by the producer when a new slab is appended.</summary>
        internal Slab? Next { get; set; }

        /// <summary>
        /// The global position that <c>Entries[0]</c> maps to. Set once when the slab is allocated or recycled. For example, if the
        /// slab length is 4096 and this is the third slab, <c>LogicalStartPosition</c> is 8192.
        /// </summary>
        internal long LogicalStartPosition { get; set; }
    }

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

    // ═══════════════════════════ FIELDS ══════════════════════════════════════

    /// <summary>Number of items per slab. Defaults to <see cref="SlabSizeHelper.SlabLength"/> (LOH-aware power-of-2 sizing).
    /// Tests may provide a smaller value via the internal constructor to make multi-slab scenarios easy to exercise without
    /// producing thousands of items.</summary>
    private readonly int _slabLength;

    // ── Volatile cursors (SPSC synchronization points) ──────────────────────

    /// <summary>How many items the producer has appended (and published). Written by producer via Volatile.Write; read by
    /// consumer via Volatile.Read.</summary>
    private long _producerPosition;

    /// <summary>How many items the consumer has released back. Written by consumer via Volatile.Write; read by producer via
    /// Volatile.Read during cleanup.</summary>
    private long _consumerPosition;

    // ── Producer-owned state ────────────────────────────────────────────────

    /// <summary>First slab in the chain — the reclamation walk starts here.</summary>
    private Slab _headSlab;

    /// <summary>Last slab in the chain — new items are appended here.</summary>
    private Slab _tailSlab;

    /// <summary>How far the producer has cleared (slots zeroed, items handed to the cleanup handler). Always
    /// <c>&lt;= _consumerPosition</c>.</summary>
    private long _cleanupPosition;

    // ── Consumer-owned state ────────────────────────────────────────────────

    /// <summary>Cached slab pointer for the consumer's current read position. Advanced forward when the consumer releases items
    /// past a slab boundary. Always at or ahead of the cleanup frontier, so it is never a recycled slab.</summary>
    private Slab _consumerSlab;

    // ── Slab recycling (producer-thread-only) ───────────────────────────────

    /// <summary>LIFO stack of slabs whose items have been fully cleaned. The producer pops from here before allocating a fresh
    /// slab, giving recently-returned slabs the best chance of still being in CPU cache.</summary>
    private readonly Stack<Slab> _freeSlabs = new();

    // ═══════════════════════════ CONSTRUCTORS ════════════════════════════════

    /// <summary>Creates a <see cref="ProducerConsumerQueue{T}"/> with the default LOH-aware slab length.</summary>
    public ProducerConsumerQueue() : this(SlabSizeHelper.SlabLength)
    {
    }

    /// <summary>Creates a <see cref="ProducerConsumerQueue{T}"/> with a caller-specified slab length. Intended for tests that need
    /// small slabs to easily exercise multi-slab edge cases.</summary>
    internal ProducerConsumerQueue(int slabLength)
    {
        _slabLength = slabLength;
        var slab = this.AllocateSlab();
        slab.LogicalStartPosition = 0;
        _headSlab = slab;
        _tailSlab = slab;
        _consumerSlab = slab;
    }

    // ═══════════════════════════ PRODUCER THREAD ═════════════════════════════

    /// <summary>
    /// Appends an item to the tail of the queue and publishes it immediately. The volatile write of the producer position ensures
    /// the item's data and any new slab's <c>Next</c> link are visible to the consumer before it can read the updated position.
    /// </summary>
    public void ProducerAppend(in T item)
    {
        var position = _producerPosition;
        var offset = (int)(position % _slabLength);

        if (offset == 0 && position > 0)
        {
            var newSlab = this.AllocateSlab();
            newSlab.LogicalStartPosition = position;
            _tailSlab.Next = newSlab;
            _tailSlab = newSlab;
        }

        _tailSlab.Entries[offset] = item;
        Volatile.Write(ref _producerPosition, position + 1);
    }

    /// <summary>
    /// Walks items from the cleanup frontier up to the current consumer position, calling the handler for each item before clearing
    /// the slot. When all items in a slab have been cleaned, the slab is returned to the free stack for reuse.
    ///
    /// The handler is responsible for domain-specific cleanup. See <see cref="ICleanupHandler"/> for details.
    /// </summary>
    public void ProducerCleanup<THandler>(ref THandler handler)
        where THandler : struct, ICleanupHandler
    {
        var consumerPosition = Volatile.Read(ref _consumerPosition);

        while (_cleanupPosition < consumerPosition)
        {
            // Advance past any fully-cleaned head slab. This handles both the normal case (we just crossed a boundary)
            // and the deferred case (a previous cleanup reached the boundary but Next was null at the time — the
            // producer has since created the next slab).
            if (_cleanupPosition >= _headSlab.LogicalStartPosition + _slabLength
                && _headSlab.Next is not null)
            {
                var oldHead = _headSlab;
                _headSlab = _headSlab.Next;
                this.RecycleSlab(oldHead);
            }

            var offset = (int)(_cleanupPosition - _headSlab.LogicalStartPosition);

            handler.HandleCleanup(_cleanupPosition, in _headSlab.Entries[offset]);
            _headSlab.Entries[offset] = default!;

            _cleanupPosition++;
        }
    }

    // ═══════════════════════════ CONSUMER THREAD ═════════════════════════════

    /// <summary>
    /// Returns a segment of all items published since the last <see cref="ConsumerRelease"/> call. Returns an empty segment when
    /// no new items are available.
    ///
    /// Internally performs a single volatile read of the producer position (acquire fence), ensuring all item data written before
    /// the producer's corresponding release fence is visible.
    /// </summary>
    public Segment ConsumerAcquire()
    {
        var published = Volatile.Read(ref _producerPosition);
        var consumed = _consumerPosition;

        var count = published - consumed;
        if (count == 0)
            return default;

        while (_consumerSlab.LogicalStartPosition + _slabLength <= consumed)
            _consumerSlab = _consumerSlab.Next!;

        var offset = (int)(consumed - _consumerSlab.LogicalStartPosition);
        return new Segment(_consumerSlab, offset, count, _slabLength);
    }

    /// <summary>
    /// Signals that the consumer has finished processing the acquired segment. Advances the consumer position via a volatile write
    /// (release fence), making the items eligible for cleanup by the producer.
    /// </summary>
    public void ConsumerRelease(in Segment segment)
    {
        if (segment.IsEmpty)
            return;

        var newPosition = _consumerPosition + segment.Count;

        while (_consumerSlab.LogicalStartPosition + _slabLength <= newPosition
               && _consumerSlab.Next is not null)
        {
            _consumerSlab = _consumerSlab.Next;
        }

        Volatile.Write(ref _consumerPosition, newPosition);
    }

    // ═══════════════════════════ SLAB MANAGEMENT ════════════════════════════

    private Slab AllocateSlab()
    {
        if (_freeSlabs.TryPop(out var slab))
        {
            Debug.Assert(slab.Next is null);
            return slab;
        }

        return new Slab(_slabLength);
    }

    private void RecycleSlab(Slab slab)
    {
        slab.Next = null;
        slab.LogicalStartPosition = 0;
        _freeSlabs.Push(slab);
    }

    // ═══════════════════════════ TEST ACCESSOR ═══════════════════════════════

    internal readonly struct TestAccessor(ProducerConsumerQueue<T> queue)
    {
        public long ProducerPosition => Volatile.Read(ref queue._producerPosition);
        public long ConsumerPosition => Volatile.Read(ref queue._consumerPosition);
        public long CleanupPosition => queue._cleanupPosition;
        public int SlabLength => queue._slabLength;
        public Slab HeadSlab => queue._headSlab;
        public Slab TailSlab => queue._tailSlab;
        public int FreeSlabCount => queue._freeSlabs.Count;
        public bool HasFreeSlabs => queue._freeSlabs.Count > 0;
    }

    internal TestAccessor GetTestAccessor() => new(this);
}
