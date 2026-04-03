using System.Diagnostics;

namespace Fabrica.Core.Collections;

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
public sealed class WorkStealingDeque<T>
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

    // ── Debug thread-ownership tracking ──────────────────────────────────────

#if DEBUG
    private int _ownerThreadId = -1;

    private void AssertOwnerThread()
    {
        var current = Environment.CurrentManagedThreadId;
        if (_ownerThreadId == -1)
            _ownerThreadId = current;
        else
            Debug.Assert(
                _ownerThreadId == current,
                $"WorkStealingDeque<{typeof(T).Name}> owner operation called from thread {current} " +
                $"but owner is thread {_ownerThreadId}. Push and TryPop are single-owner operations.");
    }
#endif

    // ── Debug injection points for deterministic interleaving tests ──────────

#if DEBUG
    /// <summary>Called in <see cref="TryPop"/> after detecting this is the last element (top == bottom) but before the CAS that
    /// claims it. Tests can inject a steal here to force the CAS-lost path. Note: at this point <c>_bottom</c> has already been
    /// decremented, so a normal <see cref="TrySteal"/> will see the deque as empty. Use
    /// <see cref="TestAccessor.SimulateStealCas"/> to directly advance <c>_top</c>, simulating a thief whose steal was already
    /// in flight before the owner decremented bottom.</summary>
    internal Action? DebugBeforePopCas { get; set; }

    /// <summary>Called in <see cref="TrySteal"/> after reading the item but before the CAS that claims it. Tests can inject
    /// another steal or pop here to force the CAS-lost path.</summary>
    internal Action? DebugBeforeStealCas { get; set; }
#endif

    // ═══════════════════════════ CONSTRUCTORS ═════════════════════════════════

    /// <summary>Creates a deque with the specified initial capacity. Capacity is rounded up to the next power of 2
    /// (minimum 4).</summary>
    internal WorkStealingDeque(int initialCapacity = DefaultInitialCapacity)
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
#if DEBUG
        this.AssertOwnerThread();
#endif

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
#if DEBUG
        this.AssertOwnerThread();
#endif

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
                this.DebugBeforePopCas?.Invoke();
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
            this.DebugBeforeStealCas?.Invoke();
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

    /// <summary>Current capacity of the backing buffer. May increase after growth; never decreases.</summary>
    internal int Capacity => _buffer.Capacity;

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
