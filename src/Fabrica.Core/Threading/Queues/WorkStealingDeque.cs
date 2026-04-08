namespace Fabrica.Core.Threading.Queues;

/// <summary>
/// Lock-free work-stealing deque (Chase-Lev algorithm). Designed for job-pool schedulers where one owner thread pushes and
/// pops items while multiple thief threads concurrently steal items.
///
/// THREAD MODEL
///   Exactly one thread is the "owner." The owner calls <see cref="Push"/> (bottom, LIFO) and <see cref="TryPop"/> (bottom,
///   LIFO). Any number of "thief" threads may call <see cref="TrySteal"/> (top, FIFO) concurrently with each other and with the
///   owner.
///
///   The LIFO/FIFO asymmetry is deliberate: the owner pops the most recently pushed item (likely still in cache), while thieves
///   steal the oldest item (typically a larger, unsplit task in recursive/fork-join workloads).
///
///   In DEBUG builds, the owner thread ID is captured on the first <see cref="Push"/> or <see cref="TryPop"/> call and asserted
///   on all subsequent owner operations. This catches accidental cross-thread misuse early.
///
/// MEMORY MODEL
///   <c>_bottom</c>: Written only by the owner. Volatile writes ensure visibility to thieves. The owner may read its own writes
///   without volatile (single-writer guarantee).
///
///   <c>_top</c>: Read and written by both owner and thieves. All mutations use <see cref="Interlocked.CompareExchange(ref long, long, long)"/>
///   (full barrier). Reads use <see cref="Volatile.Read(ref long)"/> (acquire fence).
///
///   <c>_buffer</c>: A reference to the current <see cref="RingBuffer"/>. Written only by the owner (during growth) with a
///   volatile write. Read by thieves with a volatile read. Both old and new buffers contain valid data for live indices, so
///   a thief that reads a stale buffer reference still accesses correct values.
///
///   A <see cref="Thread.MemoryBarrier"/> (sequential-consistency fence) is placed between the bottom-store and top-load in
///   <see cref="TryPop"/>, and between the top-load and bottom-load in <see cref="TrySteal"/>. This prevents the subtle
///   last-element race where both owner and thief believe they can take the final item.
///
/// GROWTH
///   When the owner's push would exceed the current buffer capacity, the buffer is doubled. All live items (from top to bottom)
///   are copied into a new, larger <see cref="RingBuffer"/>. The old buffer is not recycled — it remains valid for any in-flight
///   steal operations that already hold a reference to it. Once those operations complete and no more references exist, the old
///   buffer becomes eligible for garbage collection. Because each growth doubles the capacity, the system quickly converges on a
///   steady-state buffer size once the maximum concurrent depth is reached, after which no further allocations or GC pressure
///   occurs.
///
/// REFERENCE
///   D. Chase and Y. Lev, "Dynamic Circular Work-Stealing Deque," SPAA 2005.
///   N. M. Lê et al., "Correct and Efficient Work-Stealing for Weak Memory Models," PPoPP 2013.
/// </summary>
internal sealed class WorkStealingDeque<T>
{
    private const int DefaultInitialCapacity = 32;
    private const int MinimumCapacity = 4;

    // ── Volatile cursors ─────────────────────────────────────────────────────

    /// <summary>Bottom index — written only by the owner. Points one past the last item (the next push slot).</summary>
    private long _bottom;

    /// <summary>Top index — read/written by owner and thieves via CAS. Points to the oldest stealable item.</summary>
    private long _top;

    // ── Buffer ───────────────────────────────────────────────────────────────

    /// <summary>Current circular buffer. Grown by the owner when full; read by thieves during steal.</summary>
    private RingBuffer _buffer;

    private SingleThreadedOwner _owner;

    // ── Debug injection points for deterministic interleaving tests ──────────

#if DEBUG
    private Action? _debugBeforePopCas;
    private Action? _debugBeforeStealCas;
#endif

    // ═══════════════════════════ CONSTRUCTORS ═════════════════════════════════

    public WorkStealingDeque() : this(DefaultInitialCapacity)
    {
    }

    private WorkStealingDeque(int initialCapacity)
    {
        var capacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(initialCapacity, MinimumCapacity));
        _buffer = new RingBuffer(capacity);
    }

    // ═══════════════════════════ OWNER OPERATIONS ════════════════════════════

    /// <summary>
    /// Pushes an item onto the bottom of the deque (LIFO end). Owner-only — must not be called concurrently.
    /// </summary>
    public void Push(T item)
    {
        _owner.AssertOwnerThread();

        // Read the owner's bottom (no volatile needed — single writer) and the thieves' top (acquire fence — need to see
        // the latest steals so we know how many live items exist).
        var bottom = _bottom;
        var top = Volatile.Read(ref _top);
        var buffer = _buffer;

        // If the live item count (bottom - top) has filled the current buffer, grow before writing. Growth doubles the
        // buffer and copies all live items, so after this the new buffer has room.
        if (bottom - top >= buffer.Capacity)
            buffer = this.Grow(bottom, top);

        // Write the item into the ring buffer at the current bottom slot. The mask converts the ever-increasing bottom
        // index into a physical array index via bitwise AND (works because capacity is a power of 2).
        buffer.Items[bottom & buffer.Mask] = item;

        // Release fence (Volatile.Write): ensures the item store above is visible to thieves before they can observe the
        // incremented bottom. A thief reading bottom via Volatile.Read (acquire) will see the item.
        Volatile.Write(ref _bottom, bottom + 1);
    }

    /// <summary>
    /// Pops an item from the bottom of the deque (LIFO end). Owner-only — must not be called concurrently with itself
    /// (concurrent steals are safe).
    ///
    /// Returns <c>true</c> if an item was popped, <c>false</c> if the deque was empty or a thief won the race for the last item.
    /// </summary>
    public bool TryPop(out T item)
    {
        _owner.AssertOwnerThread();

        // Speculatively decrement bottom. This "claims" the slot at (bottom - 1). We must publish this decrement before
        // reading top so that a concurrent thief sees the reduced range and doesn't also try to take the same item.
        var bottom = _bottom - 1;
        var buffer = Volatile.Read(ref _buffer);
        Volatile.Write(ref _bottom, bottom);

        // Sequential-consistency fence: the Volatile.Write above is only a release fence (prevents upward reordering of
        // prior stores). We need a full fence here to prevent the subsequent Volatile.Read of _top from being reordered
        // before the _bottom store. Without this, both owner and thief could observe stale values and both believe they
        // can take the last item.
        Thread.MemoryBarrier();

        var top = Volatile.Read(ref _top);

        if (top <= bottom)
        {
            item = buffer.Items[bottom & buffer.Mask];

            if (top == bottom)
            {
                // This is the last element. Both the owner (us, via pop) and thieves (via steal) may be competing for it.
                // Use CAS on _top to resolve the race: whoever successfully advances top from t to t+1 wins the item.
#if DEBUG
                _debugBeforePopCas?.Invoke();
#endif
                if (Interlocked.CompareExchange(ref _top, top + 1, top) != top)
                {
                    // A thief won the race and already took this item. Restore bottom to the empty state and report
                    // failure.
                    Volatile.Write(ref _bottom, bottom + 1);
                    item = default!;
                    return false;
                }

                // We won the race. Restore bottom so that top == bottom (empty deque). This write is safe without volatile
                // because the next Push will publish it; no thief reads bottom in the empty state productively.
                Volatile.Write(ref _bottom, bottom + 1);
            }

            return true;
        }

        // The deque was already empty when we read top. Restore bottom to undo the speculative decrement.
        Volatile.Write(ref _bottom, bottom + 1);
        item = default!;
        return false;
    }

    // ═══════════════════════════ THIEF OPERATIONS ════════════════════════════

    /// <summary>
    /// Steals an item from the top of the deque (FIFO end). Thread-safe — may be called concurrently by any number of thieves,
    /// and concurrently with the owner's <see cref="Push"/> and <see cref="TryPop"/>.
    ///
    /// Returns <c>true</c> if an item was stolen, <c>false</c> if the deque was empty or another thief/owner won the race.
    /// </summary>
    public bool TrySteal(out T item)
    {
        // Read top first (acquire fence), then enforce ordering with a full fence, then read bottom (acquire fence).
        // This ordering is critical: if we read bottom first and it's stale-high (the owner has since decremented it),
        // we might think there's an item when the owner has already claimed it.
        var top = Volatile.Read(ref _top);
        Thread.MemoryBarrier();
        var bottom = Volatile.Read(ref _bottom);

        if (top < bottom)
        {
            // There appears to be at least one item. Read it from the buffer. We use Volatile.Read on _buffer because the
            // owner may have grown (replaced) the buffer since we last saw it, and we need to see either the old buffer
            // (which still contains valid data at this index) or the new buffer.
            var buffer = Volatile.Read(ref _buffer);
            item = buffer.Items[top & buffer.Mask];

            // Atomically advance top to claim this item. If another thief (or the owner in a last-element pop) already
            // advanced top past our snapshot, the CAS fails and we report failure — no item was lost, someone else got it.
#if DEBUG
            _debugBeforeStealCas?.Invoke();
#endif
            if (Interlocked.CompareExchange(ref _top, top + 1, top) != top)
            {
                item = default!;
                return false;
            }

            return true;
        }

        item = default!;
        return false;
    }

    // ═══════════════════════════ BATCH STEAL ═════════════════════════════════

    /// <summary>
    /// Steals approximately half the items from this deque into <paramref name="destination"/>, returning one item
    /// directly for immediate execution. Adapted from Tokio's <c>steal_into2</c> (queue.rs) and Go's <c>runqsteal</c>.
    ///
    /// <para>
    /// This is the key optimization for fan-out scenarios (e.g. a barrier completing 64 dependent jobs that all land on
    /// one worker's deque). Instead of 15 idle workers each stealing one item via CAS on a single <c>_top</c>, the first
    /// stealer takes half (~32), the second takes half of what remains (~16), etc. Distribution becomes logarithmic:
    /// ~log2(N) rounds instead of ~N sequential CAS operations.
    /// </para>
    ///
    /// THREAD MODEL
    ///   Called by a thief thread. <paramref name="destination"/> must be the thief's own deque (the thief is the single
    ///   producer for its deque, so writing to <c>destination._bottom</c> and its buffer is safe). Multiple thieves may
    ///   call this concurrently on the same victim — at most one succeeds (CAS on <c>_top</c>), others return false.
    ///
    /// ALGORITHM
    ///   1. Read victim's _top (acquire) and _bottom (acquire), with a full fence between them (same ordering as TrySteal).
    ///   2. Compute n = ceil(available / 2). Steal the older half, leaving the newer half for the owner.
    ///   3. Read source buffer and copy all n items into the destination buffer BEFORE the CAS. The writes into the
    ///      destination are invisible to other threads because destination._bottom has not yet been advanced. This is
    ///      critical: once the CAS advances source._top, the owner may immediately push new items that wrap around into
    ///      the ring buffer slots we just read from.
    ///   4. CAS victim's _top from top to top+n, atomically claiming the range [top, top+n). If the CAS fails (another
    ///      thief or the owner's TryPop won the race), discard and return false.
    ///   5. Publish destination._bottom += (n-1) with a release store, making the stolen items visible.
    ///
    /// CORRECTNESS
    ///   - Items are read from the source BEFORE the CAS. While _top == top, the range [top, top+n) is safe to read:
    ///     the owner only writes at _bottom (above our range), and other stealers only advance _top via CAS (which would
    ///     cause our CAS to fail, discarding our reads).
    ///   - After a successful CAS, the owner may push new items that wrap into the slots we read. This is safe because
    ///     we already copied the items out.
    ///   - On CAS failure, the items written to the destination buffer are harmless: destination._bottom was not advanced,
    ///     so those slots appear empty to stealers, and the owner (thief) will overwrite them on future pushes.
    ///   - The destination writes are safe because the thief is the single producer (Push/TryPop owner) of its own deque.
    ///   - If the destination's buffer is too small, it is grown before copying (same Grow logic as Push).
    /// </summary>
    /// <returns><c>true</c> if at least one item was stolen; <c>false</c> if the deque was empty or the CAS lost the race.</returns>
    public bool TryStealHalf(WorkStealingDeque<T> destination, out T firstItem)
    {
        var top = Volatile.Read(ref _top);
        Thread.MemoryBarrier();
        var bottom = Volatile.Read(ref _bottom);

        var available = bottom - top;
        if (available <= 0)
        {
            firstItem = default!;
            return false;
        }

        // Steal ceil(available/2): the older half from the top.
        var n = available - (available / 2);

        var srcBuffer = Volatile.Read(ref _buffer);

        // Read the first item (will be returned directly for immediate execution).
        firstItem = srcBuffer.Items[top & srcBuffer.Mask];

        // Copy remaining n-1 items into the destination's buffer. These writes are invisible to other threads
        // because destination._bottom has not been advanced yet.
        var itemsToTransfer = n - 1;
        if (itemsToTransfer > 0)
        {
            destination._owner.AssertOwnerThread();

            var dstBottom = destination._bottom;
            var dstTop = Volatile.Read(ref destination._top);
            var dstBuffer = destination._buffer;

            // Grow may need multiple doublings since we're inserting a batch, not a single item.
            while (dstBottom - dstTop + itemsToTransfer >= dstBuffer.Capacity)
                dstBuffer = destination.Grow(dstBottom, dstTop);

            for (var i = 1L; i < n; i++)
            {
                var srcIdx = (top + i) & srcBuffer.Mask;
                var dstIdx = (dstBottom + i - 1) & dstBuffer.Mask;
                dstBuffer.Items[dstIdx] = srcBuffer.Items[srcIdx];
            }
        }

        // Now atomically claim the range. If this fails, another thief (or owner's TryPop) already advanced _top.
        if (Interlocked.CompareExchange(ref _top, top + n, top) != top)
        {
            firstItem = default!;
            return false;
        }

        // Publish the stolen items in the destination deque.
        if (itemsToTransfer > 0)
        {
            var dstBottom = destination._bottom;
            Volatile.Write(ref destination._bottom, dstBottom + itemsToTransfer);
        }

        return true;
    }

    // ═══════════════════════════ DIAGNOSTICS ═════════════════════════════════

    /// <summary>
    /// Approximate number of items in the deque. Because <c>_bottom</c> and <c>_top</c> are read as two separate volatile loads
    /// (not as an atomic pair), a concurrent push, pop, or steal can change one index between the two reads. The returned value
    /// may therefore not correspond to any single point-in-time state of the deque — this is what "not linearizable" means.
    /// Intended for diagnostics and testing only; do not use for correctness-critical decisions.
    /// </summary>
    public long Count
    {
        get
        {
            var bottom = Volatile.Read(ref _bottom);
            var top = Volatile.Read(ref _top);
            return Math.Max(0, bottom - top);
        }
    }

    /// <summary>Returns <c>true</c> if the deque appears empty. Same non-linearizable caveats as <see cref="Count"/>.</summary>
    public bool IsEmpty => this.Count == 0;

    private int Capacity => _buffer.Capacity;

    // ═══════════════════════════ BUFFER MANAGEMENT ═══════════════════════════

    /// <summary>
    /// Doubles the buffer capacity and copies all live items (from <paramref name="top"/> to <paramref name="bottom"/>) into the
    /// new buffer. The old buffer is not freed — in-flight steals may still reference it, and it becomes eligible for GC once
    /// those references are dropped.
    /// </summary>
    private RingBuffer Grow(long bottom, long top)
    {
        var oldBuffer = _buffer;
        var newCapacity = oldBuffer.Capacity * 2;
        var newBuffer = new RingBuffer(newCapacity);

        for (var index = top; index < bottom; index++)
            newBuffer.Items[index & newBuffer.Mask] = oldBuffer.Items[index & oldBuffer.Mask];

        // Release fence: ensures all item copies are visible before thieves can observe the new buffer reference.
        // GC-RELIANCE: This write drops the deque's last strong reference to oldBuffer; the GC collects it once no in-flight steal
        // still holds a loaded pointer to that RingBuffer. In Rust/C++: defer freeing the old buffer (epoch-based reclamation or
        // hazard pointers) until no thief can Volatile.Read the previous _buffer, or use Arc/RC on the buffer.
        Volatile.Write(ref _buffer, newBuffer);
        return newBuffer;
    }

    // ═══════════════════════════ TEST ACCESSOR ═══════════════════════════════

    internal TestAccessor GetTestAccessor()
        => new(this);

    /// <summary>
    /// Provides internal access to the deque's state for testing. Used to simulate specific race conditions deterministically.
    /// </summary>
    internal struct TestAccessor(WorkStealingDeque<T> deque)
    {
        public static WorkStealingDeque<T> Create(int initialCapacity) => new(initialCapacity);

        public readonly int Capacity => deque.Capacity;

#if DEBUG
        public readonly Action? DebugBeforePopCas
        {
            get => deque._debugBeforePopCas;
            set => deque._debugBeforePopCas = value;
        }

        public readonly Action? DebugBeforeStealCas
        {
            get => deque._debugBeforeStealCas;
            set => deque._debugBeforeStealCas = value;
        }
#endif

        /// <summary>
        /// Simulates the final CAS step of a steal that was already in flight (i.e., the thief had already read top and bottom
        /// and confirmed <c>top &lt; bottom</c> before the owner decremented bottom). This directly advances <c>_top</c> and
        /// reads the item, bypassing the <c>top &lt; bottom</c> check that would fail because the owner has already
        /// decremented <c>_bottom</c>.
        ///
        /// Returns <c>true</c> if the simulated steal succeeded (CAS won), <c>false</c> if another CAS already advanced top.
        /// </summary>
        public readonly bool SimulateStealCas(out T item)
        {
            var top = Volatile.Read(ref deque._top);
            var buffer = Volatile.Read(ref deque._buffer);
            item = buffer.Items[top & buffer.Mask];
            return Interlocked.CompareExchange(ref deque._top, top + 1, top) == top;
        }
    }

    // ═══════════════════════════ RING BUFFER ═════════════════════════════════

    /// <summary>
    /// Power-of-2 circular buffer used as the backing store for the deque.
    ///
    /// The <see cref="Mask"/> enables O(1) index wrapping via bitwise AND (e.g., <c>index &amp; Mask</c> is equivalent to
    /// <c>index % Capacity</c> but avoids the cost of integer division).
    ///
    /// Old buffers are kept alive (not recycled) because in-flight steal operations may still hold a reference to a previous
    /// buffer. Since indices are global (never reset), a thief reading from an old buffer at a valid index sees the correct item —
    /// the grow operation copies all live items to the new buffer at the same logical indices.
    /// </summary>
    internal sealed class RingBuffer(int capacity)
    {
        /// <summary>The backing array storing deque items. Indexed by <c>globalIndex &amp; <see cref="Mask"/></c>.</summary>
        public readonly T[] Items = new T[capacity];

        /// <summary>Total number of slots in this buffer. Always a power of 2.</summary>
        public readonly int Capacity = capacity;

        /// <summary><c>Capacity - 1</c>. Used for fast modular indexing via bitwise AND.</summary>
        public readonly long Mask = capacity - 1;
    }
}
