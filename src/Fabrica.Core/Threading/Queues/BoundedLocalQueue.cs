using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Fabrica.Core.Threading.Queues;

/// <summary>
/// Fixed-capacity work-stealing queue modeled after Tokio's multi-thread scheduler local queue.
/// Packs two cursors (steal and real) into a single atomic <c>long</c> so that pop and steal
/// serialize through the same CAS target, eliminating the TOCTOU race inherent in Chase-Lev
/// batch steal.
///
/// LAYOUT
///
///   <c>_head</c>: <c>long</c> — packed <c>[steal:32 | real:32]</c>
///     <c>steal</c> (high 32 bits): stealers advance this via CAS to complete a batch steal.
///     <c>real</c> (low 32 bits): owner advances via CAS on pop; stealers advance during Phase 1
///     of batch steal to claim items.
///     Invariant: <c>steal &lt;= real &lt;= tail</c> (modulo wrapping).
///
///   <c>_tail</c>: <c>int</c> — owner-written (<see cref="Volatile.Write(ref int, int)"/>),
///     thief-read (<see cref="Volatile.Read(ref int)"/>). Points one past the last item pushed
///     to the ring buffer.
///
///   <c>_buffer</c>: <c>T?[256]</c> — fixed ring buffer, indexed by <c>(index &amp; MASK)</c>.
///
///   <c>_lifoSlot</c>: <c>T?</c> — single-slot LIFO bypass. The most recently pushed item lives
///     here for cache-hot pops. Accessed atomically via <see cref="Interlocked.Exchange{T}"/>.
///
/// THREAD MODEL
///
///   One owner thread: <see cref="Push"/>, <see cref="TryPop"/>.
///   Any number of thief threads: <see cref="TrySteal"/>, <see cref="TryStealHalf"/>.
///   Thieves may call concurrently with each other and with the owner.
///
/// BATCH STEAL PROTOCOL (Two-Phase CAS)
///
///   Phase 1 — Claim: CAS <c>_head</c> to advance <c>real</c> by n, leaving <c>steal</c>
///   behind. This claims items <c>[steal, steal+n)</c> and signals "steal in progress"
///   (<c>steal != real</c>). The owner can still pop (advancing <c>real</c> further from
///   <c>real+n</c> onward), but other stealers bail immediately.
///
///   Copy: read the n claimed items from the source buffer and write them to the destination.
///   These writes are invisible because the destination's <c>_tail</c> has not been advanced.
///
///   Phase 2 — Complete: CAS <c>_head</c> to advance <c>steal</c> to match the current
///   <c>real</c> (which may have been further advanced by the owner's pops). This signals
///   "steal complete" and re-enables other stealers.
///
/// POP PROTOCOL
///
///   CAS <c>_head</c> to advance <c>real</c> by 1. If <c>steal == real</c> (no steal in
///   progress), advance both together. If <c>steal != real</c> (steal in progress), advance
///   only <c>real</c>. Because pop and steal both CAS the same packed word, they cannot
///   silently overlap — if either side moves, the other's CAS fails and retries.
///
/// OVERFLOW
///
///   When the ring buffer is full (<c>tail - steal &gt;= Capacity</c>), the owner moves half
///   the items plus the incoming item to a global injection queue via the <c>_onOverflow</c>
///   callback. Follows Tokio's algorithm:
///
///   1. If <c>steal != real</c> (a concurrent steal is in progress, which will free capacity
///      soon): inject only the incoming item.
///   2. If <c>steal == real</c>: CAS <c>_head</c> to advance both cursors by
///      <c>Capacity / 2</c>, claiming the oldest half. Read those items from the buffer and
///      inject them along with the incoming item. If the CAS fails (a thief moved), retry
///      from step 1.
///
///   The incoming item is always injected (never written to the ring buffer on overflow),
///   matching Tokio's <c>push_back_or_overflow</c> + <c>push_overflow</c>.
///
/// BUFFER ACCESS PATTERN — SPECULATIVE READ BEFORE CAS
///
///   Both <see cref="TryPop"/> and <see cref="TrySteal"/> read the buffer slot BEFORE
///   the CAS on <c>_head</c>. This is required for <see cref="TrySteal"/> because once
///   the CAS succeeds (freeing a slot at the head), the owner's <see cref="Push"/> can
///   immediately overwrite the same physical slot — the ring is circular, and when full,
///   <c>tail &amp; Mask == head &amp; Mask</c>. Reading first captures the value while the
///   full-ring invariant still protects the slot. If the CAS fails, the speculative read
///   is simply discarded on retry.
///
///   Tokio's <c>pop()</c> does the opposite: CAS first, then read. This is safe because
///   <c>pop</c> is owner-only and sequential with <c>push</c> — no concurrent writer can
///   overwrite the slot between the CAS and the read. Tokio has no single-item steal;
///   its batch <c>steal_into</c> uses a two-phase CAS that prevents owner ring writes
///   during the copy phase (Phase 1 sets <c>steal != real</c>, forcing the owner's push
///   into the inject-only overflow path).
///
///   We use read-before-CAS uniformly in both <see cref="TryPop"/> and
///   <see cref="TrySteal"/> for consistency. Both orderings are correct for
///   <see cref="TryPop"/> (owner-only); only read-before-CAS is correct for
///   <see cref="TrySteal"/> (multi-threaded).
///
///   Nulling consumed slots (for GC) is safe in <see cref="TryPop"/> (owner-only, sequential
///   with push). In <see cref="TrySteal"/>, we skip nulling because a concurrent push may
///   have already overwritten the slot with a newer item — at most 256 stale references are
///   held until the next push cycle overwrites them.
///
/// REFERENCE
///   Tokio scheduler queue: tokio/src/runtime/scheduler/multi_thread/queue.rs
/// </summary>
internal sealed class BoundedLocalQueue<T>(Action<T>? onOverflow = null) where T : class
{
    internal const int QueueCapacity = 256;
    private const int Mask = QueueCapacity - 1;
    private const int OverflowBatchSize = QueueCapacity / 2;

    /// <summary>
    /// Packed head: high 32 bits = steal cursor, low 32 bits = real cursor.
    /// All mutations via <see cref="Interlocked.CompareExchange(ref long, long, long)"/>.
    /// </summary>
    private long _head;

    /// <summary>Tail index. Written by the owner, read by thieves.</summary>
    private int _tail;

    /// <summary>Fixed ring buffer. Indexed by <c>index &amp; MASK</c>.</summary>
    private readonly T?[] _buffer = new T?[QueueCapacity];

    /// <summary>
    /// Single-slot LIFO bypass. The most recently pushed item goes here so the owner can
    /// pop it without CAS contention. Accessed atomically via Interlocked.Exchange so
    /// thieves can steal it when the ring buffer is empty.
    /// </summary>
    private T? _lifoSlot;

    /// <summary>
    /// Called when the ring buffer is full to inject overflow items into a global queue.
    /// Receives each item individually. The caller is responsible for batching wake
    /// notifications. Null means overflow is not supported (asserts in DEBUG).
    /// </summary>
    private readonly Action<T>? _onOverflow = onOverflow;

    private SingleThreadedOwner _owner;

    // ═══════════════════════════ PACKING HELPERS ══════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Pack(int steal, int real)
        => ((long)(uint)steal << 32) | (uint)real;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int Steal, int Real) Unpack(long packed)
        => ((int)(packed >>> 32), (int)(uint)packed);

    /// <summary>Unsigned distance: handles 32-bit wrapping correctly.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Distance(int from, int to)
        => (int)((uint)from - (uint)to);

    // ═══════════════════════════ OWNER OPERATIONS ════════════════════════════

    /// <summary>
    /// Pushes an item. The new item goes to the LIFO slot; any evicted item is appended to
    /// the ring buffer tail.
    /// </summary>
    public void Push(T item)
    {
        _owner.AssertOwnerThread();

        var prev = Interlocked.Exchange(ref _lifoSlot, item);
        if (prev == null)
            return;

        this.PushToRingBuffer(prev);
    }

    private void PushToRingBuffer(T item)
    {
        var tail = _tail;

        while (true)
        {
            var head = Volatile.Read(ref _head);
            var (steal, real) = Unpack(head);

            if (Distance(tail, steal) < QueueCapacity)
                break;

            // Ring buffer is full. Follow Tokio's overflow algorithm.
            if (_onOverflow == null)
            {
                Debug.Fail("BoundedLocalQueue overflow — no overflow callback configured.");
                return;
            }

            if (steal != real)
            {
                // A steal is in progress — it will free capacity soon. Just inject
                // the single item to the global queue (matches Tokio's behavior).
                _onOverflow(item);
                return;
            }

            // Self-steal: CAS _head to advance both steal and real by half the
            // capacity, claiming the oldest half of the ring buffer.
            var next = Pack(steal + OverflowBatchSize, real + OverflowBatchSize);
            if (Interlocked.CompareExchange(ref _head, next, head) != head)
                continue;

            // Successfully claimed OverflowBatchSize items from [steal, steal + N).
            // The overflow CAS serializes with steal CASes, so these slots are
            // exclusively ours. Read each and inject to the global queue.
            for (var i = 0; i < OverflowBatchSize; i++)
            {
                var idx = (steal + i) & Mask;
                _onOverflow(_buffer[idx]!);
            }

            // Also inject the incoming item (it does not go to the ring buffer).
            _onOverflow(item);
            return;
        }

        _buffer[tail & Mask] = item;
        Volatile.Write(ref _tail, tail + 1);
    }

    /// <summary>
    /// Pops an item. Checks the LIFO slot first (no CAS, cache-hot). Falls back to CAS-popping
    /// from the ring buffer head.
    /// </summary>
    public bool TryPop(out T item)
    {
        _owner.AssertOwnerThread();

        var lifo = Interlocked.Exchange(ref _lifoSlot, null);
        if (lifo != null)
        {
            item = lifo;
            return true;
        }

        var head = Volatile.Read(ref _head);

        while (true)
        {
            var (steal, real) = Unpack(head);
            var tail = _tail;

            if (real == tail)
            {
                item = default!;
                return false;
            }

            // If no steal in progress (steal == real), advance both cursors together.
            // If a steal is in progress (steal != real), advance only real — the stealer
            // owns the range [steal, real) and will complete Phase 2 later.
            var nextReal = real + 1;
            var next = steal == real
                ? Pack(nextReal, nextReal)
                : Pack(steal, nextReal);

            // Speculative read: must happen BEFORE the CAS. Once we CAS _head
            // (freeing a slot at the head), the owner's push can immediately
            // overwrite this physical slot (tail wraps to the same index).
            // Reading first captures the value while it's still protected by
            // the full ring invariant (Distance == QueueCapacity).
            var value = _buffer[real & Mask]!;

            var prev = Interlocked.CompareExchange(ref _head, next, head);
            if (prev == head)
            {
                _buffer[real & Mask] = null;
                item = value;
                return true;
            }

            head = prev;
        }
    }

    // ═══════════════════════════ THIEF OPERATIONS ════════════════════════════

    /// <summary>
    /// Steals a single item. Tries the ring buffer first, then the LIFO slot.
    /// </summary>
    public bool TrySteal(out T item)
    {
        var head = Volatile.Read(ref _head);

        while (true)
        {
            var (steal, real) = Unpack(head);

            if (steal != real)
            {
                // Another steal is in progress — bail.
                item = default!;
                return false;
            }

            var tail = Volatile.Read(ref _tail);

            if (real == tail)
            {
                // Ring buffer empty. Try LIFO slot as a last resort.
                var lifo = Interlocked.Exchange(ref _lifoSlot, null);
                if (lifo != null)
                {
                    item = lifo;
                    return true;
                }

                item = default!;
                return false;
            }

            // Speculative read before CAS — same rationale as TryPop.
            var value = Volatile.Read(ref _buffer[real & Mask]);

            var next = Pack(real + 1, real + 1);
            var prev = Interlocked.CompareExchange(ref _head, next, head);
            if (prev == head)
            {
                item = value!;
                return true;
            }

            head = prev;
        }
    }

    /// <summary>
    /// Steals approximately half the items, placing them in <paramref name="destination"/> and
    /// returning one item directly. Uses the two-phase CAS protocol (see class doc).
    /// </summary>
    public bool TryStealHalf(BoundedLocalQueue<T> destination, out T firstItem)
    {
        destination._owner.AssertOwnerThread();

        var dstTail = destination._tail;

        // Bail if the destination doesn't have room for a half-batch.
        var (dstSteal, _) = Unpack(Volatile.Read(ref destination._head));
        if (Distance(dstTail, dstSteal) > QueueCapacity / 2)
        {
            firstItem = default!;
            return false;
        }

        var prevPacked = Volatile.Read(ref _head);
        long nextPacked;
        int n;

        // ── Phase 1: Claim ──────────────────────────────────────────────────
        // CAS _head to advance real by n, leaving steal behind. This reserves
        // [steal, steal+n) and signals "steal in progress" to other threads.
        while (true)
        {
            var (srcSteal, srcReal) = Unpack(prevPacked);

            if (srcSteal != srcReal)
            {
                firstItem = default!;
                return false;
            }

            var srcTail = Volatile.Read(ref _tail);
            var available = Distance(srcTail, srcReal);

            if (available <= 0)
            {
                var lifo = Interlocked.Exchange(ref _lifoSlot, null);
                if (lifo != null)
                {
                    firstItem = lifo;
                    return true;
                }

                firstItem = default!;
                return false;
            }

            n = available - (available / 2);

            nextPacked = Pack(srcSteal, srcReal + n);

            var result = Interlocked.CompareExchange(ref _head, nextPacked, prevPacked);
            if (result == prevPacked)
                break;

            prevPacked = result;
        }

        Debug.Assert(n <= QueueCapacity / 2);

        // ── Copy ────────────────────────────────────────────────────────────
        // The claimed items are at [first, first+n) in the source buffer.
        // Copy all n into the destination. We'll extract one for direct return after.
        var (first, _) = Unpack(nextPacked);

        for (var i = 0; i < n; i++)
        {
            var srcIdx = (first + i) & Mask;
            var dstIdx = (dstTail + i) & Mask;
            destination._buffer[dstIdx] = Volatile.Read(ref _buffer[srcIdx]);
        }

        // ── Phase 2: Complete ───────────────────────────────────────────────
        // Advance steal to match current real (which the owner may have further
        // advanced via pops). This re-enables other stealers.
        var phase2Packed = nextPacked;
        while (true)
        {
            var (_, currentReal) = Unpack(phase2Packed);
            var completePacked = Pack(currentReal, currentReal);

            var phase2Result = Interlocked.CompareExchange(ref _head, completePacked, phase2Packed);
            if (phase2Result == phase2Packed)
                break;

            phase2Packed = phase2Result;
        }

        // Take the last copied item for direct return; publish the rest.
        n -= 1;
        var retIdx = (dstTail + n) & Mask;
        firstItem = destination._buffer[retIdx]!;
        destination._buffer[retIdx] = null;

        if (n > 0)
            Volatile.Write(ref destination._tail, dstTail + n);

        return true;
    }

    // ═══════════════════════════ DIAGNOSTICS ═════════════════════════════════

    /// <summary>
    /// Approximate item count. Not linearizable — concurrent operations may change the
    /// count between the reads of <c>_head</c>, <c>_tail</c>, and <c>_lifoSlot</c>.
    /// </summary>
    public int Count
    {
        get
        {
            var (_, real) = Unpack(Volatile.Read(ref _head));
            var tail = Volatile.Read(ref _tail);
            var lifo = Volatile.Read(ref _lifoSlot) != null ? 1 : 0;
            return Math.Max(0, Distance(tail, real)) + lifo;
        }
    }

    /// <summary>Approximate emptiness check. Same caveats as <see cref="Count"/>.</summary>
    public bool IsEmpty => this.Count == 0;

    // ═══════════════════════════ TEST ACCESSOR ═══════════════════════════════

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor(BoundedLocalQueue<T> queue)
    {
        public int Capacity => QueueCapacity;
        public int RingCount
        {
            get
            {
                var (_, real) = Unpack(Volatile.Read(ref queue._head));
                var tail = Volatile.Read(ref queue._tail);
                return Math.Max(0, Distance(tail, real));
            }
        }

        public bool HasLifoItem => Volatile.Read(ref queue._lifoSlot) != null;
    }
}
