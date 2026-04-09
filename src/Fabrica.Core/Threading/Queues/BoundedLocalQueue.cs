using System.Diagnostics;
using System.Runtime.CompilerServices;
#if UNSAFE_OPT
using System.Runtime.InteropServices;
#endif

namespace Fabrica.Core.Threading.Queues;

#if UNSAFE_OPT
/// <summary>
/// Cache-line padding for the packed head and tail cursors. Places <see cref="Head"/> at offset 0
/// and <see cref="Tail"/> at offset 128, so they sit on different Apple Silicon cache lines. This
/// eliminates false sharing between thieves (who CAS <see cref="Head"/>) and the owner (who writes
/// <see cref="Tail"/>).
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct CacheLinePaddedHead
{
    [FieldOffset(0)]
    internal long Head;

    [FieldOffset(128)]
    internal int Tail;
}

/// <summary>
/// Fixed-size inline ring buffer. Stored directly inside <see cref="BoundedLocalQueue{T}"/> with
/// no heap allocation — elements occupy contiguous memory within the struct.
/// </summary>
[InlineArray(256)]
internal struct RingBuffer<T>
{
    private T _element0;
}
#else
internal struct CacheLinePaddedHead
{
    internal long Head;
    internal int Tail;
}
#endif

/// <summary>
/// Fixed-capacity work-stealing queue modeled after Tokio's multi-thread scheduler local queue.
/// Packs two cursors (steal and real) into a single atomic <c>long</c> so that pop and steal
/// serialize through the same CAS target, eliminating the TOCTOU race inherent in Chase-Lev
/// batch steal.
///
/// MEMORY LAYOUT
///
///   This is a value type (struct) intended to be embedded inline in a heap-allocated owner (e.g.
///   <c>WorkerContext</c>). The ring buffer is an <see cref="InlineArray"/> — no separate heap
///   allocation. The head and tail cursors are separated by 128 bytes via
///   <see cref="CacheLinePaddedHead"/> to eliminate false sharing on Apple Silicon.
///
///   <c>_headTail.Head</c>: <c>long</c> — packed <c>[steal:32 | real:32]</c>
///     <c>steal</c> (high 32 bits): stealers advance this via CAS to complete a batch steal.
///     <c>real</c> (low 32 bits): owner advances via CAS on pop; stealers advance during Phase 1
///     of batch steal to claim items.
///     Invariant: <c>steal &lt;= real &lt;= tail</c> (modulo wrapping).
///
///   <c>_headTail.Tail</c>: <c>int</c> — owner-written (<see cref="Volatile.Write(ref int, int)"/>),
///     thief-read (<see cref="Volatile.Read(ref int)"/>). Points one past the last item pushed
///     to the ring buffer. Sits on a separate cache line from <c>_headTail.Head</c>.
///
///   <c>_buffer</c>: <c>RingBuffer&lt;T?&gt;</c> — inline 256-element ring buffer, indexed by
///     <c>(index &amp; MASK)</c>.
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
///   Because this is a mutable struct, all access must go through the field reference (e.g.
///   <c>context.Deque.Push(...)</c> where <c>Deque</c> is a non-readonly field of a class).
///   Never copy a live queue — the copy would share no state with the original.
///
/// BATCH STEAL PROTOCOL (Two-Phase CAS)
///
///   Phase 1 — Claim: CAS <c>_headTail.Head</c> to advance <c>real</c> by n, leaving <c>steal</c>
///   behind. This claims items <c>[steal, steal+n)</c> and signals "steal in progress"
///   (<c>steal != real</c>). The owner can still pop (advancing <c>real</c> further from
///   <c>real+n</c> onward), but other stealers bail immediately.
///
///   Copy: read the n claimed items from the source buffer and write them to the destination.
///   These writes are invisible because the destination's <c>_headTail.Tail</c> has not been advanced.
///
///   Phase 2 — Complete: CAS <c>_headTail.Head</c> to advance <c>steal</c> to match the current
///   <c>real</c> (which may have been further advanced by the owner's pops). This signals
///   "steal complete" and re-enables other stealers.
///
/// POP PROTOCOL
///
///   CAS <c>_headTail.Head</c> to advance <c>real</c> by 1. If <c>steal == real</c> (no steal in
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
///   2. If <c>steal == real</c>: CAS <c>_headTail.Head</c> to advance both cursors by
///      <c>Capacity / 2</c>, claiming the oldest half. Read those items from the buffer and
///      inject them along with the incoming item. If the CAS fails (a thief moved), retry
///      from step 1.
///
///   The incoming item is always injected (never written to the ring buffer on overflow),
///   matching Tokio's <c>push_back_or_overflow</c> + <c>push_overflow</c>.
///
/// BUFFER ACCESS PATTERN — SPECULATIVE READ BEFORE CAS
///
///   <see cref="TryPop"/> reads the buffer slot BEFORE the CAS on <c>_headTail.Head</c>.
///   This is done for consistency with the steal path (where it is required). Once a CAS
///   succeeds (freeing a slot at the head), the owner's <see cref="Push"/> can immediately
///   overwrite the same physical slot — the ring is circular, and when full,
///   <c>tail &amp; Mask == head &amp; Mask</c>. Reading first captures the value while the
///   full-ring invariant still protects the slot. If the CAS fails, the speculative read
///   is simply discarded on retry.
///
/// REFERENCE
///   Tokio scheduler queue: tokio/src/runtime/scheduler/multi_thread/queue.rs
/// </summary>
internal struct BoundedLocalQueue<T>(StrongBox<InjectionQueue<T>> overflow) where T : class
{
    internal const int QueueCapacity = 256;
    private const int Mask = QueueCapacity - 1;
    private const int OverflowBatchSize = QueueCapacity / 2;

    /// <summary>
    /// Cache-line-padded cursors: <c>Head</c> (packed steal|real) at offset 0, <c>Tail</c> at
    /// offset 128. All Head mutations via <see cref="Interlocked.CompareExchange(ref long, long, long)"/>;
    /// Tail via <see cref="Volatile.Write(ref int, int)"/>.
    /// </summary>
    private CacheLinePaddedHead _headTail;

    /// <summary>
    /// Single-slot LIFO bypass. The most recently pushed item goes here so the owner can
    /// pop it without CAS contention. Accessed atomically via Interlocked.Exchange so
    /// thieves can steal it when the ring buffer is empty.
    /// </summary>
    private T? _lifoSlot;

#if UNSAFE_OPT
    /// <summary>Inline ring buffer (256 elements, no heap allocation). Indexed by <c>index &amp; MASK</c>.</summary>
    private RingBuffer<T?> _buffer;
#else
    /// <summary>Heap-allocated ring buffer (256 elements). Indexed by <c>index &amp; MASK</c>.</summary>
    private readonly T?[] _buffer = new T?[QueueCapacity];
#endif

    /// <summary>
    /// Shared injection queue. When the ring buffer is full, overflow items are enqueued here.
    /// </summary>
    private readonly StrongBox<InjectionQueue<T>> _overflow = overflow;

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

    /// <summary>
    /// Ring slot by logical index. After <c>rawIndex &amp; Mask</c> the offset is always in
    /// <c>[0, 255]</c>. Under <c>UNSAFE_OPT</c>, <see cref="Unsafe.Add"/> avoids redundant JIT bounds
    /// checks on the inline buffer; otherwise plain array indexing (bounds-checked).
    /// </summary>
    /// <remarks>
    /// Static <c>ref</c> helper: struct instance methods cannot return <c>ref</c> to instance fields
    /// (CS8170).
    /// </remarks>
#if UNSAFE_OPT
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref T? BufferAt(ref BoundedLocalQueue<T> queue, int rawIndex) =>
        ref Unsafe.Add(ref Unsafe.As<RingBuffer<T?>, T?>(ref queue._buffer), rawIndex & Mask);
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref T? BufferAt(ref BoundedLocalQueue<T> queue, int rawIndex) =>
        ref queue._buffer[rawIndex & Mask];
#endif

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
        // Plain read: _headTail.Tail is only written by the owner (us). No fence needed.
        var tail = _headTail.Tail;

        while (true)
        {
            // Volatile.Read: acquire fence to see the latest _headTail.Head written by thieves'
            // CAS (Phase 1/2) or owner's pop CAS. Without this, we could see a stale
            // head and incorrectly believe the ring is full.
            var head = Volatile.Read(ref _headTail.Head);
            var (steal, real) = Unpack(head);

            if (Distance(tail, steal) < QueueCapacity)
                break;

            if (steal != real)
            {
                // A steal is in progress — it will free capacity soon. Just inject
                // the single item to the global queue (matches Tokio's behavior).
                _overflow.Value.Enqueue(item);
                return;
            }

            // CAS: atomically advance both steal and real by half the capacity, claiming
            // the oldest half. Must be CAS (not plain write) because thieves may
            // concurrently CAS _headTail.Head for their own steal. If a thief moved first, our
            // comparand is stale and we retry the overflow decision.
            var next = Pack(steal + OverflowBatchSize, real + OverflowBatchSize);
            if (Interlocked.CompareExchange(ref _headTail.Head, next, head) != head)
                continue;

            // Successfully claimed OverflowBatchSize items from [steal, steal + N).
            // The overflow CAS serializes with steal CASes, so these slots are
            // exclusively ours. Inject them plus the incoming item to the global queue.
            this.PushOverflow(steal, item);
            return;
        }

        // Plain write: this slot is beyond what any thief can see — thieves read up to
        // _headTail.Tail, which hasn't been advanced yet. No concurrent reader can access this index.
        BufferAt(ref this, tail) = item;
        // Volatile.Write: release fence ensures the buffer write above is globally visible
        // before thieves see the new tail. Without this, a thief could read the new tail
        // via Volatile.Read(_headTail.Tail) but see a stale/null buffer slot.
        Volatile.Write(ref _headTail.Tail, tail + 1);
    }

    /// <summary>
    /// Moves <see cref="OverflowBatchSize"/> claimed items from the ring buffer plus the
    /// incoming item to the shared injection queue under a single lock acquisition.
    /// Kept out-of-line to minimize the code size of the hot <see cref="PushToRingBuffer"/> path.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly void PushOverflow(int steal, T item)
    {
        ReadOnlySpan<T?> span = _buffer;
        var start = steal & Mask;
        var firstLen = Math.Min(OverflowBatchSize, QueueCapacity - start);

        var seg1 = span.Slice(start, firstLen);
        var seg2 = firstLen < OverflowBatchSize
            ? span[..(OverflowBatchSize - firstLen)]
            : [];

        _overflow.Value.EnqueueBatch(seg1, seg2, item);
    }

    /// <summary>
    /// Pops an item. Checks the LIFO slot first (no CAS, cache-hot). Falls back to CAS-popping
    /// from the ring buffer head. Returns <c>null</c> if the queue is empty.
    /// </summary>
    public T? TryPop()
    {
        _owner.AssertOwnerThread();

        // Interlocked: thieves may concurrently Interlocked.Exchange _lifoSlot (in
        // TryStealHalf). Atomic swap ensures exactly one thread gets the item.
        // Volatile.Read first: dominant path is empty slot — skip expensive Exchange when null.
        if (Volatile.Read(ref _lifoSlot) != null)
        {
            var lifo = Interlocked.Exchange(ref _lifoSlot, null);
            if (lifo != null)
                return lifo;
        }

        // Volatile.Read: acquire fence to see the latest _headTail.Head from thieves' CAS
        // operations. Loaded once before the loop; updated from CAS return on retry.
        var head = Volatile.Read(ref _headTail.Head);

        while (true)
        {
            var (steal, real) = Unpack(head);
            // Plain read: _headTail.Tail is only written by the owner (us). No fence needed.
            var tail = _headTail.Tail;

            if (real == tail)
                return null;

            // If no steal in progress (steal == real), advance both cursors together.
            // If a steal is in progress (steal != real), advance only real — the stealer
            // owns the range [steal, real) and will complete Phase 2 later.
            var nextReal = real + 1;
            var next = steal == real
                ? Pack(nextReal, nextReal)
                : Pack(steal, nextReal);

            // Plain read (speculative): must happen BEFORE the CAS below. The slot is
            // safe to read because _headTail.Head hasn't moved yet — no push can overwrite it.
            // After the CAS frees this slot, a concurrent push could immediately reuse
            // the physical index (ring wraps). Reading first captures the value while
            // the full-ring invariant still protects the slot.
            var value = BufferAt(ref this, real);

            // CAS: atomically advance real (and steal if no steal in progress).
            // Serializes with thieves' CAS on the same _headTail.Head. If a thief moved _headTail.Head
            // since our read, the comparand is stale and we retry with the new value.
            var prev = Interlocked.CompareExchange(ref _headTail.Head, next, head);
            if (prev == head)
            {
                // Plain null: owner-only; safe because we just advanced head past this
                // slot. No thief can read it (they'd need to CAS _headTail.Head which now
                // points beyond this index). Next push will overwrite it anyway.
                BufferAt(ref this, real) = null;
                return value;
            }

            head = prev;
        }
    }

    // ═══════════════════════════ THIEF OPERATIONS ════════════════════════════

    /// <summary>
    /// Steals approximately half the items, placing them in <paramref name="destination"/> and
    /// returning one item directly. Uses the two-phase CAS protocol (see type doc).
    /// Returns <c>null</c> if nothing was available to steal.
    /// </summary>
    public T? TryStealHalf(ref BoundedLocalQueue<T> destination)
    {
        destination._owner.AssertOwnerThread();

        // Plain read: destination._headTail.Tail is only written by the destination's owner,
        // which is the current thread (verified by AssertOwnerThread above).
        var dstTail = destination._headTail.Tail;

        // Volatile.Read: acquire fence to see the latest destination _headTail.Head. Another
        // thief could have concurrently stolen from the destination, advancing its head.
        var (dstSteal, _) = Unpack(Volatile.Read(ref destination._headTail.Head));
        if (Distance(dstTail, dstSteal) > QueueCapacity / 2)
            return null;

        // Volatile.Read: acquire fence to see the latest source _headTail.Head. The owner or
        // another thief may have CAS'd it. Loaded once; updated from CAS return on retry.
        var prevPacked = Volatile.Read(ref _headTail.Head);
        long nextPacked;
        int n;

        // ── Phase 1: Claim ──────────────────────────────────────────────────
        // CAS _headTail.Head to advance real by n, leaving steal behind. This reserves
        // [steal, steal+n) and signals "steal in progress" to other threads.
        while (true)
        {
            var (srcSteal, srcReal) = Unpack(prevPacked);

            if (srcSteal != srcReal)
                return null;

            // Volatile.Read: acquire fence to see the latest _headTail.Tail written by the owner.
            // The owner's Volatile.Write(_headTail.Tail) pairs with this read, ensuring we see
            // the buffer contents that were written before the tail was advanced.
            var srcTail = Volatile.Read(ref _headTail.Tail);
            var available = Distance(srcTail, srcReal);

            if (available <= 0)
            {
                // Interlocked: owner may concurrently write _lifoSlot via Push. Atomic
                // swap ensures exactly one thread (owner or thief) gets the item.
                return Interlocked.Exchange(ref _lifoSlot, null);
            }

            n = available - (available / 2);

            nextPacked = Pack(srcSteal, srcReal + n);

            // CAS (Phase 1): atomically advance real by n while leaving steal behind.
            // This claims [steal, steal+n) exclusively and signals "steal in progress"
            // (steal != real). Serializes with owner's pop CAS and other thieves.
            var result = Interlocked.CompareExchange(ref _headTail.Head, nextPacked, prevPacked);
            if (result == prevPacked)
                break;

            prevPacked = result;
        }

        Debug.Assert(n <= QueueCapacity / 2);

        // ── Copy (bulk) ────────────────────────────────────────────────────
        // The claimed range [steal, steal+n) is exclusively ours (Phase 1 CAS).
        // The owner cannot push (steal != real forces overflow) or pop these slots
        // (TryPop advances real, beyond our range). No other thief can start
        // (steal != real causes early return). The Phase 1 CAS provides a full
        // fence ensuring we see the owner's buffer writes. Matches Tokio's
        // ptr::read (plain read) in the equivalent Rust code.
        //
        // Bulk copy + clear: each circular range is at most 2 contiguous segments
        // (before and after the buffer wrap point). The while loop handles all
        // combinations in 1–3 iterations, using Span.CopyTo/Clear instead of
        // per-element reads/writes.
        var (first, _) = Unpack(nextPacked);
        var srcStart = first & Mask;
        var dstStart = dstTail & Mask;
        var remaining = n;

        Span<T?> srcSpan = _buffer;
        Span<T?> dstSpan = destination._buffer;

        while (remaining > 0)
        {
            var chunk = Math.Min(remaining, Math.Min(QueueCapacity - srcStart, QueueCapacity - dstStart));
            var src = srcSpan.Slice(srcStart, chunk);
            src.CopyTo(dstSpan.Slice(dstStart, chunk));
            src.Clear();

            remaining -= chunk;
            srcStart = (srcStart + chunk) & Mask;
            dstStart = (dstStart + chunk) & Mask;
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
            var phase2Result = Interlocked.CompareExchange(ref _headTail.Head, completePacked, phase2Packed);
            if (phase2Result == phase2Packed)
                break;

            var (actualSteal, actualReal) = Unpack(phase2Result);
            Debug.Assert(actualSteal != actualReal, "Phase 2 CAS failed but steal == real — invariant violated.");
            phase2Packed = phase2Result;
        }

        // Plain read + null: destination is owned by the current thread, and
        // destination._headTail.Tail hasn't been advanced, so no other thread can see these slots.
        n -= 1;
        var retIdx = (dstTail + n) & Mask;
        ref var retSlot = ref BufferAt(ref destination, retIdx);
        var firstItem = retSlot;
        retSlot = null;

        if (n > 0)
            // Volatile.Write: release fence ensures all buffer writes in the copy loop
            // above are globally visible before the destination's tail is advanced. This
            // pairs with Volatile.Read(_headTail.Tail) in any thread that pops/steals from the
            // destination, guaranteeing they see the copied items.
            Volatile.Write(ref destination._headTail.Tail, dstTail + n);

        return firstItem;
    }

    // ═══════════════════════════ DIAGNOSTICS ═════════════════════════════════

    /// <summary>
    /// Approximate item count. Not linearizable — concurrent operations may change the
    /// count between the reads of <c>_headTail.Head</c>, <c>_headTail.Tail</c>, and <c>_lifoSlot</c>.
    /// </summary>
    public int Count
    {
        get
        {
            // All Volatile.Read: Count can be called from any thread. Acquire fences
            // ensure we see the latest values written by the owner's Volatile.Write
            // (_headTail.Tail), the owner/thief CAS (_headTail.Head), and Interlocked.Exchange (_lifoSlot).
            var (_, real) = Unpack(Volatile.Read(ref _headTail.Head));
            var tail = Volatile.Read(ref _headTail.Tail);
            var lifo = Volatile.Read(ref _lifoSlot) != null ? 1 : 0;
            return Math.Max(0, Distance(tail, real)) + lifo;
        }
    }

    /// <summary>Approximate emptiness check. Same caveats as <see cref="Count"/>.</summary>
    public bool IsEmpty => this.Count == 0;

    // ═══════════════════════════ TEST HELPERS ═════════════════════════════════

    internal int RingCount
    {
        get
        {
            // Volatile.Read: same rationale as Count — callable from any thread.
            var (_, real) = Unpack(Volatile.Read(ref _headTail.Head));
            var tail = Volatile.Read(ref _headTail.Tail);
            return Math.Max(0, Distance(tail, real));
        }
    }

    // Volatile.Read: may be called from a non-owner thread in tests.
    internal bool HasLifoItem => Volatile.Read(ref _lifoSlot) != null;
}
