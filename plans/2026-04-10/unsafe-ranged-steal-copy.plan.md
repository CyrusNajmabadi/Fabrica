---
name: Unsafe ranged steal copy
overview: Replace the Span.Slice/CopyTo/Clear pattern in TryStealHalf with MemoryMarshal.CreateSpan-based ranged copy (avoiding Slice bounds checks) and skip clearing source slots under UNSAFE_OPT since ring validity is entirely index-based.
todos:
  - id: impl
    content: "Replace UNSAFE_OPT copy block in TryStealHalf: MemoryMarshal.CreateSpan ranged copy, skip clear"
    status: completed
  - id: test
    content: Run BoundedLocalQueueTests in Debug + Release
    status: completed
  - id: bench
    content: Run RealisticTickBenchmark + JIT disasm comparison
    status: completed
isProject: false
---

# Safe Ranged Copy for TryStealHalf (no clear under UNSAFE_OPT)

## Context

Master uses `Span.Slice/CopyTo/Clear` in a 1-3 iteration loop. PR #223 replaced that with per-element `BufferAt` copies. Both have overhead: master pays for `BulkMoveWithWriteBarrier` + `Span.Clear` + Slice bounds checks; the PR pays for per-element `CORINFO_HELP_CHECKED_ASSIGN_REF` + `str xzr`.

## Two optimizations

### 1. MemoryMarshal.CreateSpan from Unsafe.Add refs (avoid Slice bounds checks)

Instead of `srcSpan.Slice(srcStart, chunk)` which validates start+length against the Span bounds, create spans directly from `Unsafe.Add` refs via `MemoryMarshal.CreateSpan`. This gives us the same ranged `CopyTo` (which still goes through `BulkMoveWithWriteBarrier` — preserving GC write barriers) but skips the Slice bounds-check overhead.

```csharp
#if UNSAFE_OPT
while (remaining > 0)
{
    var chunk = Math.Min(remaining, Math.Min(QueueCapacity - srcStart, QueueCapacity - dstStart));

    var src = MemoryMarshal.CreateSpan(
        ref Unsafe.Add(ref Unsafe.As<RingBuffer<T?>, T?>(ref _buffer), srcStart), chunk);
    src.CopyTo(MemoryMarshal.CreateSpan(
        ref Unsafe.Add(ref Unsafe.As<RingBuffer<T?>, T?>(ref destination._buffer), dstStart), chunk));

    remaining -= chunk;
    srcStart = (srcStart + chunk) & Mask;
    dstStart = (dstStart + chunk) & Mask;
}
#else
// Debug: Span-based copy + clear (existing code, unchanged).
#endif
```

### 2. Skip clearing source slots under UNSAFE_OPT

Ring buffer validity is **entirely index-based** (head/tail cursors). No code checks ring slots for null to determine emptiness. `PushOverflow` (line 274) already reads ring slots without clearing them — establishing precedent. Stale references keep objects alive until slots are overwritten by future pushes, but with a 256-slot ring under high throughput this is negligible. Pooled jobs are returned to the pool immediately after execution anyway.

Under debug (non-`UNSAFE_OPT`), the existing `src.Clear()` is retained so that stale null references cause immediate crashes if an index bug ever reads a freed slot.

## Files to modify

- [src/Fabrica.Core/Threading/Queues/BoundedLocalQueue.cs](src/Fabrica.Core/Threading/Queues/BoundedLocalQueue.cs) — Replace the copy block in `TryStealHalf` (~line 419-449) with `#if UNSAFE_OPT` / `#else` paths: the unsafe path uses `MemoryMarshal.CreateSpan` + `CopyTo` with no clear; the debug path keeps the existing Span.Slice + CopyTo + Clear.

## Expected JIT impact

- `BulkMoveWithWriteBarrier` is still called (GC-safe), but the `Span.Slice` range-check preamble and the `Span.Clear` call + its helper (`blr x2`) should be eliminated entirely.
- Code size should shrink; the `CORINFO_HELP_POLL_GC` and `CORINFO_HELP_RNGCHKFAIL` blocks from Span bounds checks should disappear.

## Verification

- Run existing `BoundedLocalQueueTests` in both Debug and Release
- Benchmark with `RealisticTickBenchmark`
- JIT disassembly comparison (master vs branch, no-PGO and PGO)
