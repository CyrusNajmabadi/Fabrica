# Tokio-style Fixed-Capacity Local Queue with Packed-Atomic Head

## Motivation

The current Chase-Lev `WorkStealingDeque<T>` has a fundamental correctness flaw in its
`TryStealHalf` implementation. Chase-Lev uses two separate atomics: `_top` (CAS'd by stealers)
and `_bottom` (written by the owner). The owner's non-last-item `TryPop` never touches `_top`,
so a concurrent `TryStealHalf` CAS on `_top` cannot detect or prevent the owner's pop. No amount
of re-reading `_bottom` before the CAS fully closes the TOCTOU window — a thread preemption
between the read and the CAS allows the owner to pop arbitrarily many items into the thief's
claimed range, producing **duplicate items**.

This was caught by the stress test:

```
Duplicate item detected: 99157
  Stress_OwnerPushPop_MultipleThievesStealHalf_NoItemsLost(
    itemCount: 100000, initialCapacity: 64, thiefCount: 3, popEveryN: 5)
```

The root cause is architectural: Chase-Lev was designed for single-item steal. Batch steal
(steal-half) requires both pop and steal to serialize through the same atomic, which Chase-Lev
does not provide.

## Design: Packed-Head Fixed-Capacity Queue

Replace `WorkStealingDeque<T>` with a Tokio-style `BoundedLocalQueue<T>` that packs two cursors
into a single atomic word. Both pop and steal CAS this word, making them mutually exclusive by
construction.

### Data Layout

```
_head: uint (atomic, packed)
  bits [31:16] = steal   — stealers advance this via CAS
  bits [15:0]  = real    — owner advances this via CAS on pop

  Invariant: steal <= real <= tail (modulo 16-bit wrapping)
  Stealable range: [steal, real)  — what thieves can batch-claim
  Poppable range:  [real, tail)   — what the owner can claim

_tail: ushort (owner-written via Volatile.Write, thief-read via Volatile.Read)
  Points one past the last pushed item.

_buffer: T?[256] — fixed, allocated once, never grows

_lifoSlot: T? — single-slot LIFO bypass, owner-only access (no synchronization)
```

### Why 16-bit Indices

With 16-bit indices (ushort, range 0–65535) and a 256-slot buffer, wrapping is seamless:
256 divides 65536 evenly, so `index & 0xFF` always gives the correct physical slot. Unsigned
subtraction handles wrapping naturally (e.g., `tail=2, head=65534` → `2 - 65534 = 4` in ushort
arithmetic → 4 items).

### Operations

#### Push (owner-only)

1. Swap the new item into `_lifoSlot`. If an evicted item exists, continue to step 2.
2. Read `_tail` (plain — single writer) and `_head.real` (Volatile.Read of packed `_head`).
3. If `tail - real >= 256`: **overflow** — move half the ring buffer items to the global
   injection queue (see Overflow below). Re-read `_head`.
4. Write evicted item into `_buffer[tail & MASK]`.
5. `Volatile.Write(ref _tail, (ushort)(tail + 1))` — release fence makes item visible.

#### TryPop (owner-only)

1. Check `_lifoSlot`. If non-null, take it (LIFO, cache-hot). Return true.
2. Read `_head` (Volatile.Read) → extract `real`, `steal`.
3. Read `_tail` (plain). If `real == tail`: empty, return false.
4. Read item at `_buffer[real & MASK]`.
5. CAS `_head` from `pack(steal, real)` to `pack(steal, (ushort)(real + 1))`.
   - Success: return item.
   - Failure: a stealer changed `steal` — re-read `_head`, retry from step 2.

The CAS-on-every-pop is the key correctness property: because both pop and steal touch `_head`,
they cannot silently overlap.

#### TryStealHalf (thief, any thread)

1. Read `_head` (Volatile.Read) → extract `steal`, `real`.
2. Read `_tail` (Volatile.Read).
3. Compute stealable = `real - steal` (using ushort wrapping). If 0: empty, return false.
   Also read `available = tail - steal`. Compute `n = ceil(available / 2)`, capped to stealable.
4. Read first item at `_buffer[steal & MASK]` (returned directly for immediate execution).
5. Copy items `[steal+1, steal+n)` into destination's buffer (invisible — destination's tail
   not yet advanced).
6. CAS `_head` from `pack(steal, real)` to `pack((ushort)(steal + n), real)`.
   - Success: advance destination's tail, return first item.
   - Failure: another thief or the owner's pop changed `_head` — return false (no retry).

**Why this is correct**: The CAS validates the entire packed word. If the owner popped (advancing
`real`) between our read and CAS, the CAS fails. If another thief stole (advancing `steal`),
the CAS fails. No TOCTOU window exists.

#### TrySteal (thief, any thread)

Same as TryStealHalf with n=1 and no destination copy.

### Overflow (Push when full)

When `tail - head.real >= 256`, the owner moves half the items to the global injection queue:

1. Read `_head` → extract `real`.
2. Compute `n = (tail - real) / 2`.
3. CAS `_head` from `pack(steal, real)` to `pack(steal, (ushort)(real + n))` — claims the
   older half (same as a self-pop of n items).
4. Batch-inject the n claimed items into the `WorkerPool` injection queue (single lock
   acquisition for the batch).
5. Now there is room to push.

This overflow is expected to be **rare** in our game engine — we have bounded job counts per
tick (currently ~530 jobs across all phases). But it ensures correctness if we ever exceed 256
local items.

### LIFO Slot

The `_lifoSlot` preserves cache-hot LIFO behavior that Chase-Lev provides via bottom-end pops.
Without it, the ring buffer is strictly FIFO (both pop and steal advance from the head end).

- **Push**: new item goes to `_lifoSlot`; evicted item goes to ring buffer tail.
- **Pop**: check `_lifoSlot` first (instant, no CAS). If empty, CAS from ring head.
- **Steal**: thieves cannot access `_lifoSlot` — it is owner-private.

This matches Tokio's `next_task` field. The most recently pushed job (likely sharing cache lines
with the just-completed job) is always popped first without any atomic contention.

## Changes

### New Files

- `src/Fabrica.Core/Threading/Queues/BoundedLocalQueue.cs` — the new queue implementation
- `tests/Fabrica.Core.Tests/Collections/BoundedLocalQueueTests.cs` — unit tests
- `tests/Fabrica.Core.Tests/Collections/BoundedLocalQueueStressTests.cs` — stress tests

### Modified Files

- `src/Fabrica.Core/Jobs/WorkerContext.cs` — change `Deque` field from
  `WorkStealingDeque<Job>` to `BoundedLocalQueue<Job>`
- `src/Fabrica.Core/Jobs/WorkerPool.cs` — update `TryStealAndExecute` and
  `PropagateCompletion` to use new queue API. Add batch-inject support for overflow callback.
- `src/Fabrica.Core/Jobs/WorkerPool.cs` class-level doc — update WORK STEALING section

### Deleted Files

- `src/Fabrica.Core/Threading/Queues/WorkStealingDeque.cs` — replaced entirely
- `tests/Fabrica.Core.Tests/Collections/WorkStealingDequeTests.cs` — replaced
- `tests/Fabrica.Core.Tests/Collections/WorkStealingDequeStressTests.cs` — replaced

### Unchanged

- `src/Fabrica.Core/Jobs/Job.cs` — no changes needed; `NextInQueue` remains for the
  injection queue
- `src/Fabrica.Core/Jobs/JobScheduler.cs` — no changes; uses `WorkerPool` APIs only
- Global injection queue in `WorkerPool` — unchanged (already intrusive linked list with lock)

## Memory Ordering Summary

| Field | Writer | Reader | Write fence | Read fence |
|-------|--------|--------|-------------|------------|
| `_head` (packed uint) | Owner (pop), Thieves (steal) | All | `Interlocked.CompareExchange` (full barrier) | `Volatile.Read` (acquire) |
| `_tail` (ushort) | Owner only | Thieves | `Volatile.Write` (release) | `Volatile.Read` (acquire) |
| `_lifoSlot` | Owner only | Owner only | None (single-threaded) | None |
| `_buffer[i]` | Owner (push) | Thieves (steal) | Before `_tail` advance (release) | After `_tail` read (acquire) |

## Tradeoffs

| Aspect | Chase-Lev (current) | Tokio-style (proposed) |
|--------|---------------------|------------------------|
| Pop cost | `Volatile.Write` (~2ns) | `CAS` (~10ns x86, ~20ns ARM64) |
| Batch steal correctness | Broken (TOCTOU) | Formally correct (same-word CAS) |
| Queue capacity | Unbounded (dynamic growth) | 256 fixed + overflow to injection |
| Steady-state allocation | Zero (after growth converges) | Zero (fixed buffer, no growth) |
| LIFO behavior | Native (bottom-end pop) | Via `_lifoSlot` bypass |
| Code complexity | Moderate | Higher (packed head, overflow path) |

The pop-cost increase is the main tradeoff. At ~20ns on ARM64, it is negligible relative to
job execution time (microseconds). The CAS is uncontended in the common case (owner pops while
no steal is in flight), so it will hit L1 cache.

## Implementation Order

1. Implement `BoundedLocalQueue<T>` with Push, TryPop, TrySteal, TryStealHalf (assert on
   overflow — no injection wiring yet)
2. Unit tests for all operations including packed-head CAS interactions
3. Stress tests reproducing the Chase-Lev duplicate-item bug — verify they pass
4. Wire into `WorkerContext` and `WorkerPool`, replacing `WorkStealingDeque<Job>`
5. Implement overflow path (push when full → batch inject to global queue)
6. Run benchmarks — verify no performance regression
7. Delete `WorkStealingDeque<T>` and old tests

## References

- Tokio scheduler queue: `tokio/src/runtime/scheduler/multi_thread/queue.rs`
- Go runtime runqueue: `runtime/proc.go` (`runqput`, `runqsteal`, `runqgrab`)
- D. Chase and Y. Lev, "Dynamic Circular Work-Stealing Deque," SPAA 2005
