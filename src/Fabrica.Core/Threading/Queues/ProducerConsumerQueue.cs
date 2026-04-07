using System.Diagnostics;
using Fabrica.Core.Collections.Unsafe;

namespace Fabrica.Core.Threading.Queues;

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
public sealed partial class ProducerConsumerQueue<T>
{
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
    private readonly UnsafeStack<Slab> _freeSlabs = new();

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

    // ═══════════════════════════ POSITION ACCESSORS ═════════════════════════

    /// <summary>Total items appended by the producer. Volatile read — safe from any thread.</summary>
    public long ProducerPosition => Volatile.Read(ref _producerPosition);

    /// <summary>Total items the consumer has advanced past. Volatile read — safe from any thread.</summary>
    public long ConsumerPosition => Volatile.Read(ref _consumerPosition);

    // ═══════════════════════════ CONSUMER THREAD ═════════════════════════════

    /// <summary>
    /// Returns a segment of all items published since the last <see cref="ConsumerAdvance"/> call. Returns an empty segment when
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
        return new Segment(_consumerSlab, offset, count, _slabLength, consumed);
    }

    /// <summary>
    /// Advances the consumer position by <paramref name="count"/> items via a volatile write (release fence), making those items
    /// eligible for cleanup by the producer.
    ///
    /// The consumption loop typically advances by <c>segment.Count - 1</c>, holding back the last entry so it becomes the
    /// "previous" entry on the next <see cref="ConsumerAcquire"/> call. This keeps the payload alive (not eligible for cleanup)
    /// across frames for interpolation.
    /// </summary>
    public void ConsumerAdvance(long count)
    {
        if (count <= 0)
            return;

        var newPosition = _consumerPosition + count;

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
}
