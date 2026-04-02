namespace Fabrica.Pipeline;

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
///   Slabs are allocated from a single-threaded free list on the producer thread. When the producer's cleanup pass finishes
///   clearing all entries in a slab, the slab is returned to the free list for reuse. The consumer holds a cached
///   <c>_consumerSlab</c> pointer that is always at or ahead of the cleanup frontier, so a recycled slab is never read by the
///   consumer.
///
/// CACHE-LINE PADDING
///   The two volatile position fields could benefit from cache-line padding to avoid false sharing. This is omitted for now and
///   can be added in a follow-up if profiling shows contention.
/// </summary>
public sealed class SlabList<TPayload>
{
    // ── Volatile cursors (SPSC synchronization points) ──────────────────────
    private long _producerPosition;
    private long _consumerPosition;

    // ── Producer-owned state ────────────────────────────────────────────────
    private Slab<TPayload> _headSlab;
    private Slab<TPayload> _tailSlab;
    private long _cleanupPosition;

    // ── Consumer-owned state ────────────────────────────────────────────────
    private Slab<TPayload> _consumerSlab;

    // ── Slab recycling (producer-thread-only free list) ─────────────────────
    private Slab<TPayload>? _freeSlab;

    public SlabList()
    {
        var slab = this.AllocateSlab();
        slab._logicalStartPosition = 0;
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
        var offset = (int)(position & SlabSizeHelper<TPayload>.OffsetMask);

        if (offset == 0 && position > 0)
        {
            var newSlab = this.AllocateSlab();
            newSlab._logicalStartPosition = position;
            _tailSlab._next = newSlab;
            _tailSlab = newSlab;
        }

        _tailSlab.Entries[offset] = entry;
        Volatile.Write(ref _producerPosition, position + 1);
    }

    /// <summary>
    /// Walks entries from the cleanup frontier up to the current consumer position, calling the handler for each entry before
    /// clearing the slot. When all entries in a slab have been cleaned, the slab is returned to the free list for reuse.
    ///
    /// The handler is responsible for domain-specific cleanup: checking whether an entry is pinned (and copying it to a side
    /// table), or releasing the payload's resources. See <see cref="IEntryCleanupHandler{TPayload}"/> for details.
    /// </summary>
    public void ProducerCleanupReleasedEntries<THandler>(ref THandler handler)
        where THandler : struct, IEntryCleanupHandler<TPayload>
    {
        var consumerPosition = Volatile.Read(ref _consumerPosition);
        var slabLength = SlabSizeHelper<TPayload>.SlabLength;

        while (_cleanupPosition < consumerPosition)
        {
            var offset = (int)(_cleanupPosition - _headSlab._logicalStartPosition);

            handler.HandleEntry(_cleanupPosition, in _headSlab.Entries[offset]);
            _headSlab.Entries[offset] = default;

            _cleanupPosition++;

            if (_cleanupPosition >= _headSlab._logicalStartPosition + slabLength
                && _headSlab._next is not null)
            {
                var oldHead = _headSlab;
                _headSlab = _headSlab._next;
                this.RecycleSlab(oldHead);
            }
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

        var slabLength = SlabSizeHelper<TPayload>.SlabLength;
        while (_consumerSlab._logicalStartPosition + slabLength <= consumed)
            _consumerSlab = _consumerSlab._next!;

        var offset = (int)(consumed - _consumerSlab._logicalStartPosition);
        return new SlabRange<TPayload>(_consumerSlab, offset, count);
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

        var slabLength = SlabSizeHelper<TPayload>.SlabLength;
        while (_consumerSlab._logicalStartPosition + slabLength <= newPosition
               && _consumerSlab._next is not null)
        {
            _consumerSlab = _consumerSlab._next;
        }

        Volatile.Write(ref _consumerPosition, newPosition);
    }

    // ═══════════════════════════ SLAB MANAGEMENT ════════════════════════════

    private Slab<TPayload> AllocateSlab()
    {
        if (_freeSlab is not null)
        {
            var slab = _freeSlab;
            _freeSlab = slab._next;
            slab._next = null;
            return slab;
        }

        return new Slab<TPayload>(SlabSizeHelper<TPayload>.SlabLength);
    }

    private void RecycleSlab(Slab<TPayload> slab)
    {
        slab._next = _freeSlab;
        slab._logicalStartPosition = 0;
        _freeSlab = slab;
    }

    // ═══════════════════════════ TEST ACCESSOR ═══════════════════════════════

    internal readonly struct TestAccessor(SlabList<TPayload> list)
    {
        public long ProducerPosition => Volatile.Read(ref list._producerPosition);
        public long ConsumerPosition => Volatile.Read(ref list._consumerPosition);
        public long CleanupPosition => list._cleanupPosition;
        public Slab<TPayload> HeadSlab => list._headSlab;
        public Slab<TPayload> TailSlab => list._tailSlab;
        public bool HasFreeSlabs => list._freeSlab is not null;
    }

    internal TestAccessor GetTestAccessor() => new(this);
}
