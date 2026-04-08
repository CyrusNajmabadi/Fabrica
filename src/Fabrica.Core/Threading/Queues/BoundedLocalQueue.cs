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
///   Any number of thief threads: <see cref="TryStealHalf"/>.
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
///   the items plus the incoming item to a shared <see cref="InjectionQueue{T}"/>.
///   Follows Tokio's algorithm:
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
///   <see cref="TryPop"/> reads the buffer slot BEFORE the CAS on <c>_head</c>.
///   This is done for consistency with the steal path (where it is required). Once a CAS
///   succeeds (freeing a slot at the head), the owner's <see cref="Push"/> can immediately
///   overwrite the same physical slot — the ring is circular, and when full,
///   <c>tail &amp; Mask == head &amp; Mask</c>. Reading first captures the value while the
///   full-ring invariant still protects the slot. If the CAS fails, the speculative read
///   is simply discarded on retry.
///
///   Tokio's <c>pop()</c> does the opposite: CAS first, then read. This is safe because
///   <c>pop</c> is owner-only and sequential with <c>push</c> — no concurrent writer can
///   overwrite the slot between the CAS and the read. Tokio's batch <c>steal_into</c> uses
///   a two-phase CAS that prevents owner ring writes during the copy phase (Phase 1 sets
///   <c>steal != real</c>, forcing the owner's push into the inject-only overflow path).
///
///   Nulling consumed slots (for GC) is safe in <see cref="TryPop"/> (owner-only, sequential
///   with push). In <see cref="TryStealHalf"/>'s copy loop, we use
///   <see cref="Interlocked.Exchange{T}"/> to atomically read-and-null each slot. This is
///   safe because the claimed range is exclusively ours (Phase 1 CAS), and the owner cannot
///   push to these slots until Phase 2 completes (steal != real forces overflow path).
///
/// REFERENCE
///   Tokio scheduler queue: tokio/src/runtime/scheduler/multi_thread/queue.rs
/// </summary>
internal sealed class BoundedLocalQueue<T>(InjectionQueue<T> overflow) where T : class
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
    /// Shared injection queue. When the ring buffer is full, overflow items are enqueued here.
    /// </summary>
    private readonly InjectionQueue<T> _overflow = overflow;

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

        // Interlocked: thieves may concurrently read _lifoSlot via Interlocked.Exchange
        // in TryPop or TryStealHalf. Must be atomic to avoid torn reads/writes.
        var prev = Interlocked.Exchange(ref _lifoSlot, item);
        if (prev == null)
            return;

        this.PushToRingBuffer(prev);
    }

    private void PushToRingBuffer(T item)
    {
        // Plain read: _tail is only written by the owner (us). No fence needed.
        var tail = _tail;

        while (true)
        {
            // Volatile.Read: acquire fence to see the latest _head written by thieves'
            // CAS (Phase 1/2) or owner's pop CAS. Without this, we could see a stale
            // head and incorrectly believe the ring is full.
            var head = Volatile.Read(ref _head);
            var (steal, real) = Unpack(head);

            if (Distance(tail, steal) < QueueCapacity)
                break;

            if (steal != real)
            {
                // A steal is in progress — it will free capacity soon. Just inject
                // the single item to the global queue (matches Tokio's behavior).
                _overflow.Enqueue(item);
                return;
            }

            // CAS: atomically advance both steal and real by half the capacity, claiming
            // the oldest half. Must be CAS (not plain write) because thieves may
            // concurrently CAS _head for their own steal. If a thief moved first, our
            // comparand is stale and we retry the overflow decision.
            var next = Pack(steal + OverflowBatchSize, real + OverflowBatchSize);
            if (Interlocked.CompareExchange(ref _head, next, head) != head)
                continue;

            // Successfully claimed OverflowBatchSize items from [steal, steal + N).
            // The overflow CAS serializes with steal CASes, so these slots are
            // exclusively ours. Read each and inject to the global queue.
            for (var i = 0; i < OverflowBatchSize; i++)
            {
                var idx = (steal + i) & Mask;
                _overflow.Enqueue(_buffer[idx]!);
            }

            // Also inject the incoming item (it does not go to the ring buffer).
            _overflow.Enqueue(item);
            return;
        }

        // Plain write: this slot is beyond what any thief can see — thieves read up to
        // _tail, which hasn't been advanced yet. No concurrent reader can access this index.
        _buffer[tail & Mask] = item;
        // Volatile.Write: release fence ensures the buffer write above is globally visible
        // before thieves see the new tail. Without this, a thief could read the new tail
        // via Volatile.Read(_tail) but see a stale/null buffer slot.
        Volatile.Write(ref _tail, tail + 1);
    }

    /// <summary>
    /// Pops an item. Checks the LIFO slot first (no CAS, cache-hot). Falls back to CAS-popping
    /// from the ring buffer head.
    /// </summary>
    public bool TryPop(out T item)
    {
        _owner.AssertOwnerThread();

        // Interlocked: thieves may concurrently Interlocked.Exchange _lifoSlot (in
        // TryStealHalf). Atomic swap ensures exactly one thread gets the item.
        var lifo = Interlocked.Exchange(ref _lifoSlot, null);
        if (lifo != null)
        {
            item = lifo;
            return true;
        }

        // Volatile.Read: acquire fence to see the latest _head from thieves' CAS
        // operations. Loaded once before the loop; updated from CAS return on retry.
        var head = Volatile.Read(ref _head);

        while (true)
        {
            var (steal, real) = Unpack(head);
            // Plain read: _tail is only written by the owner (us). No fence needed.
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

            // Plain read (speculative): must happen BEFORE the CAS below. The slot is
            // safe to read because _head hasn't moved yet — no push can overwrite it.
            // After the CAS frees this slot, a concurrent push could immediately reuse
            // the physical index (ring wraps). Reading first captures the value while
            // the full-ring invariant still protects the slot.
            var value = _buffer[real & Mask]!;

            // CAS: atomically advance real (and steal if no steal in progress).
            // Serializes with thieves' CAS on the same _head. If a thief moved _head
            // since our read, the comparand is stale and we retry with the new value.
            var prev = Interlocked.CompareExchange(ref _head, next, head);
            if (prev == head)
            {
                // Plain null: owner-only; safe because we just advanced head past this
                // slot. No thief can read it (they'd need to CAS _head which now
                // points beyond this index). Next push will overwrite it anyway.
                _buffer[real & Mask] = null;
                item = value;
                return true;
            }

            head = prev;
        }
    }

    // ═══════════════════════════ THIEF OPERATIONS ════════════════════════════

    /// <summary>
    /// Steals approximately half the items, placing them in <paramref name="destination"/> and
    /// returning one item directly. Uses the two-phase CAS protocol (see class doc).
    /// </summary>
    public bool TryStealHalf(BoundedLocalQueue<T> destination, out T firstItem)
    {
        destination._owner.AssertOwnerThread();

        // Plain read: destination._tail is only written by the destination's owner,
        // which is the current thread (verified by AssertOwnerThread above).
        var dstTail = destination._tail;

        // Volatile.Read: acquire fence to see the latest destination _head. Another
        // thief could have concurrently stolen from the destination, advancing its head.
        var (dstSteal, _) = Unpack(Volatile.Read(ref destination._head));
        if (Distance(dstTail, dstSteal) > QueueCapacity / 2)
        {
            firstItem = default!;
            return false;
        }

        // Volatile.Read: acquire fence to see the latest source _head. The owner or
        // another thief may have CAS'd it. Loaded once; updated from CAS return on retry.
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

            // Volatile.Read: acquire fence to see the latest _tail written by the owner.
            // The owner's Volatile.Write(_tail) pairs with this read, ensuring we see
            // the buffer contents that were written before the tail was advanced.
            var srcTail = Volatile.Read(ref _tail);
            var available = Distance(srcTail, srcReal);

            if (available <= 0)
            {
                // Interlocked: owner may concurrently write _lifoSlot via Push. Atomic
                // swap ensures exactly one thread (owner or thief) gets the item.
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

            // CAS (Phase 1): atomically advance real by n while leaving steal behind.
            // This claims [steal, steal+n) exclusively and signals "steal in progress"
            // (steal != real). Serializes with owner's pop CAS and other thieves.
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
            // Interlocked.Exchange: atomically reads the slot and nulls it for GC. The
            // claimed range [steal, steal+n) is exclusively ours (Phase 1 CAS), and the
            // owner cannot push to these slots because steal != real forces the overflow
            // path. The full fence also ensures we see the value the owner wrote during
            // push (paired with Volatile.Write of _tail).
            // Plain write to destination: destination._tail hasn't been advanced yet, so
            // no thread can see these slots.
            destination._buffer[dstIdx] = Interlocked.Exchange(ref _buffer[srcIdx], null);
        }

        // ── Phase 2: Complete ───────────────────────────────────────────────
        // Advance steal to match current real (which the owner may have further
        // advanced via pops). This re-enables other stealers.
        var phase2Packed = nextPacked;
        while (true)
        {
            var (_, currentReal) = Unpack(phase2Packed);
            var completePacked = Pack(currentReal, currentReal);

            // CAS (Phase 2): advance steal to match current real, re-enabling other
            // stealers and allowing the owner to push to ring again. May retry if the
            // owner's pop advanced real since our last read (which is the only legal
            // mutation while steal != real — hence the Debug.Assert below).
            var phase2Result = Interlocked.CompareExchange(ref _head, completePacked, phase2Packed);
            if (phase2Result == phase2Packed)
                break;

            var (actualSteal, actualReal) = Unpack(phase2Result);
            Debug.Assert(actualSteal != actualReal, "Phase 2 CAS failed but steal == real — invariant violated.");
            phase2Packed = phase2Result;
        }

        // Plain read + null: destination is owned by the current thread, and
        // destination._tail hasn't been advanced, so no other thread can see these slots.
        n -= 1;
        var retIdx = (dstTail + n) & Mask;
        firstItem = destination._buffer[retIdx]!;
        destination._buffer[retIdx] = null;

        if (n > 0)
            // Volatile.Write: release fence ensures all buffer writes in the copy loop
            // above are globally visible before the destination's tail is advanced. This
            // pairs with Volatile.Read(_tail) in any thread that pops/steals from the
            // destination, guaranteeing they see the copied items.
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
            // All Volatile.Read: Count can be called from any thread. Acquire fences
            // ensure we see the latest values written by the owner's Volatile.Write
            // (_tail), the owner/thief CAS (_head), and Interlocked.Exchange (_lifoSlot).
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
                // Volatile.Read: same rationale as Count — callable from any thread.
                var (_, real) = Unpack(Volatile.Read(ref queue._head));
                var tail = Volatile.Read(ref queue._tail);
                return Math.Max(0, Distance(tail, real));
            }
        }

        // Volatile.Read: may be called from a non-owner thread in tests.
        public bool HasLifoItem => Volatile.Read(ref queue._lifoSlot) != null;
    }
}
