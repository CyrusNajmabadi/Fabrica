namespace Fabrica.Core.Collections;

/// <summary>
/// Lock-free single-producer / single-consumer (SPSC) append-only queue backed by a linked chain of contiguous array segments
/// ("slabs"). Replaces the <c>ChainNode</c> linked list for state propagation between the production and consumption threads.
///
/// DESIGN
///   The producer appends entries to the tail slab and publishes each one immediately via a volatile write of the producer
///   position. The consumer acquires all entries published since its last release and processes them as a contiguous
///   <see cref="SlabRange{TPayload}"/>. This acquire/release model is intentionally whole-range — the consumer always processes
///   everything available.
///
///   Publish-per-append was chosen over batch publishing to minimize rendering latency: when the production loop processes
///   multiple accumulated ticks in one iteration, the consumer can see each tick as soon as it is appended rather than waiting for
///   the entire batch. The cost is one volatile write per append, which on x86 is just a compiler fence and on ARM is a single
///   <c>dmb</c> barrier — negligible at typical tick rates.
///
/// MEMORY MODEL
///   Two <c>long</c> fields serve as the SPSC synchronization points:
///
///   <c>_producerPosition</c>: Volatile.Write by producer (release fence), Volatile.Read by consumer (acquire fence). The release
///   fence ensures all entry writes (payload, timestamp, slab <c>Next</c> links) are visible before the consumer reads the
///   updated position.
///
///   <c>_consumerPosition</c>: Volatile.Write by consumer (release fence), Volatile.Read by producer (acquire fence). The producer
///   reads this during cleanup to determine which entries are safe to reclaim.
///
///   Both fields have at most one writer, so no CAS or interlocked operations are needed. Staleness in either direction is
///   conservative: a stale producer position means the consumer sees fewer entries (processes them next frame); a stale consumer
///   position means the producer retains entries longer (delays cleanup, never frees prematurely).
///
/// SLAB LIFECYCLE
///   Slabs are allocated from a single-threaded free stack on the producer thread. When the producer's cleanup pass finishes
///   clearing all entries in a slab, the slab is pushed onto the free stack for reuse. The consumer holds a cached
///   <c>_consumerSlab</c> pointer that is always at or ahead of the cleanup frontier, so a recycled slab is never read by the
///   consumer.
///
/// CACHE-LINE PADDING
///   The two volatile position fields could benefit from cache-line padding to avoid false sharing. This is omitted for now and
///   can be added in a follow-up if profiling shows contention.
/// </summary>
public sealed class SlabList<TPayload>
{
    /// <summary>Number of entries per slab. Defaults to <see cref="SlabSizeHelper{TPayload}.SlabLength"/> (LOH-aware power-of-2
    /// sizing). Tests may provide a smaller value via the internal constructor to make multi-slab scenarios easy to exercise
    /// without producing thousands of entries.</summary>
    private readonly int _slabLength;

    // ── Volatile cursors (SPSC synchronization points) ──────────────────────

    /// <summary>How many entries the producer has appended (and published). Written by producer via Volatile.Write; read by
    /// consumer via Volatile.Read.</summary>
    private long _producerPosition;

    /// <summary>How many entries the consumer has released back. Written by consumer via Volatile.Write; read by producer via
    /// Volatile.Read during cleanup.</summary>
    private long _consumerPosition;

    // ── Producer-owned state ────────────────────────────────────────────────

    /// <summary>First slab in the chain — the reclamation walk starts here.</summary>
    private Slab<TPayload> _headSlab;

    /// <summary>Last slab in the chain — new entries are appended here.</summary>
    private Slab<TPayload> _tailSlab;

    /// <summary>How far the producer has cleared (slots zeroed, payloads handed to the cleanup handler). Always
    /// <c>&lt;= _consumerPosition</c>.</summary>
    private long _cleanupPosition;

    // ── Consumer-owned state ────────────────────────────────────────────────

    /// <summary>Cached slab pointer for the consumer's current read position. Advanced forward when the consumer releases entries
    /// past a slab boundary. Always at or ahead of the cleanup frontier, so it is never a recycled slab.</summary>
    private Slab<TPayload> _consumerSlab;

    // ── Slab recycling (producer-thread-only) ───────────────────────────────

    /// <summary>LIFO stack of slabs whose entries have been fully cleaned. The producer pops from here before allocating a fresh
    /// slab, giving recently-returned slabs the best chance of still being in CPU cache.</summary>
    private readonly Stack<Slab<TPayload>> _freeSlabs = new();

    /// <summary>Creates a <see cref="SlabList{TPayload}"/> with the default LOH-aware slab length.</summary>
    public SlabList() : this(SlabSizeHelper<TPayload>.SlabLength)
    {
    }

    /// <summary>Creates a <see cref="SlabList{TPayload}"/> with a caller-specified slab length. Intended for tests that need small
    /// slabs to easily exercise multi-slab edge cases.</summary>
    internal SlabList(int slabLength)
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
    /// Appends an entry to the tail of the list and publishes it immediately. The volatile write of the producer position ensures
    /// the entry's data (payload, timestamp) and any new slab's <c>Next</c> link are visible to the consumer before it can read
    /// the updated position.
    /// </summary>
    public void ProducerAppendEntry(in PipelineEntry<TPayload> entry)
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

        _tailSlab.Entries[offset] = entry;
        Volatile.Write(ref _producerPosition, position + 1);
    }

    /// <summary>
    /// Walks entries from the cleanup frontier up to the current consumer position, calling the handler for each entry before
    /// clearing the slot. When all entries in a slab have been cleaned, the slab is returned to the free stack for reuse.
    ///
    /// The handler is responsible for domain-specific cleanup: checking whether an entry is pinned (and copying it to a side
    /// table), or releasing the payload's resources. See <see cref="IEntryCleanupHandler{TPayload}"/> for details.
    /// </summary>
    public void ProducerCleanupReleasedEntries<THandler>(ref THandler handler)
        where THandler : struct, IEntryCleanupHandler<TPayload>
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

            handler.HandleEntry(_cleanupPosition, in _headSlab.Entries[offset]);
            _headSlab.Entries[offset] = default;

            _cleanupPosition++;
        }
    }

    // ═══════════════════════════ CONSUMER THREAD ═════════════════════════════

    /// <summary>
    /// Returns a range of all entries published since the last <see cref="ConsumerReleaseEntries"/> call. Returns an empty range
    /// when no new entries are available.
    ///
    /// Internally performs a single volatile read of the producer position (acquire fence), ensuring all entry data written before
    /// the producer's corresponding release fence is visible.
    /// </summary>
    public SlabRange<TPayload> ConsumerAcquireEntries()
    {
        var published = Volatile.Read(ref _producerPosition);
        var consumed = _consumerPosition;

        var count = published - consumed;
        if (count == 0)
            return default;

        while (_consumerSlab.LogicalStartPosition + _slabLength <= consumed)
            _consumerSlab = _consumerSlab.Next!;

        var offset = (int)(consumed - _consumerSlab.LogicalStartPosition);
        return new SlabRange<TPayload>(_consumerSlab, offset, count, _slabLength);
    }

    /// <summary>
    /// Signals that the consumer has finished processing the acquired range. Advances the consumer position via a volatile write
    /// (release fence), making the entries eligible for cleanup by the producer.
    /// </summary>
    public void ConsumerReleaseEntries(in SlabRange<TPayload> range)
    {
        if (range.IsEmpty)
            return;

        var newPosition = _consumerPosition + range.Count;

        while (_consumerSlab.LogicalStartPosition + _slabLength <= newPosition
               && _consumerSlab.Next is not null)
        {
            _consumerSlab = _consumerSlab.Next;
        }

        Volatile.Write(ref _consumerPosition, newPosition);
    }

    // ═══════════════════════════ SLAB MANAGEMENT ════════════════════════════

    private Slab<TPayload> AllocateSlab()
    {
        if (_freeSlabs.TryPop(out var slab))
        {
            slab.Next = null;
            return slab;
        }

        return new Slab<TPayload>(_slabLength);
    }

    private void RecycleSlab(Slab<TPayload> slab)
    {
        slab.Next = null;
        slab.LogicalStartPosition = 0;
        _freeSlabs.Push(slab);
    }

    // ═══════════════════════════ TEST ACCESSOR ═══════════════════════════════

    internal readonly struct TestAccessor(SlabList<TPayload> list)
    {
        public long ProducerPosition => Volatile.Read(ref list._producerPosition);
        public long ConsumerPosition => Volatile.Read(ref list._consumerPosition);
        public long CleanupPosition => list._cleanupPosition;
        public int SlabLength => list._slabLength;
        public Slab<TPayload> HeadSlab => list._headSlab;
        public Slab<TPayload> TailSlab => list._tailSlab;
        public int FreeSlabCount => list._freeSlabs.Count;
        public bool HasFreeSlabs => list._freeSlabs.Count > 0;
    }

    internal TestAccessor GetTestAccessor() => new(this);
}
