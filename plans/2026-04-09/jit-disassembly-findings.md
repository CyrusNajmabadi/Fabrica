# JIT Disassembly Deep Dive — Findings & Optimization Roadmap

Platform: .NET 10.0, ARM64 (Apple M4 Max), Release build, TieredCompilation=0 (FullOpts)

## Methods Analyzed

16 Fabrica methods captured via `DOTNET_JitDisasm`, grouped into 4 analysis areas:
- **Queue operations**: BoundedLocalQueue.TryPop, PushToRingBuffer, TryStealHalf
- **Scheduler dispatch**: WorkerPool.TryExecuteOne, TryStealAndExecute, TryDequeueInjected, ExecuteJob, PropagateCompletion
- **Wake/coordinator**: WorkerPool.TryWakeOneWorker, JobScheduler.RunUntilComplete
- **Job execution**: ComputeJob.Execute, BarrierJob.Execute, TriggerJob.Execute, SnapshotJob.Execute, SnapshotCollectorJob.Execute

### Key structural observations

- **Pack/Unpack/Distance**: Fully inlined everywhere (no separate method bodies) — optimal.
- **Push(T)**: Shell inlined into callers; PushToRingBuffer remains an out-of-line call — acceptable since ring push is the slow path.
- **AssertOwnerThread**: Emits `ldrsb wzr, [x19]` in Release — appears to be a null-check probe, NOT a debug assert. Confirm this is not wasted work.
- **TryExecuteOne**: Inlined into RunUntilComplete — good.
- **NotifyWorkAvailable / TransitionFromSearching**: Inlined into callers — good.
- **NumSearching / NumUnparked**: Inlined as `uxth` / `asr` — good.
- **Job.Execute**: NOT devirtualized — full vtable dispatch (`ldr x2, [x20]; ldr x2, [x2, #0x40]; ldr x2, [x2, #0x20]; blr x2`).
- **No PGO data**: All methods show "No PGO data" — enabling Dynamic PGO could help.

---

## Optimization Roadmap (Ranked by Impact)

### HIGH IMPACT

#### 1. Eliminate bounds checks in ComputeJob/SnapshotJob hash loops
**Problem:** The dominant inner loop (`ComputeJob.Execute`, called 480x/tick) has a bounds check on EVERY iteration despite `idx = (i + seed) & (len - 1)` where `len` is a power of 2:
```arm64
add     w8, w5, w4        ; idx = i + seed
and     w8, w8, w2        ; idx &= (len - 1)
cmp     w8, w1            ; BOUNDS CHECK: idx < arr.Length
bhs     RNGCHKFAIL        ; branch to throw
```
The JIT doesn't prove `& (len-1)` ensures `idx < len` because `len` is loaded from the array at runtime rather than being a constant.

**Fix:**
```csharp
ref int r0 = ref MemoryMarshal.GetArrayDataReference(arr);
// ...
Debug.Assert((uint)idx < (uint)arr.Length);
ref int slot = ref Unsafe.Add(ref r0, idx);
slot = HashMix(slot, i);
```
Alternatively, use `const int L = ArrayLength` for the mask so the JIT can prove the range.

**Impact:** HIGH — removes compare + conditional branch from the hottest loop (25,000 iterations x 480 jobs = 12M iterations/tick).

#### 2. Eliminate integer division in TryStealAndExecute steal loop
**Problem:** Each steal iteration computes `(start + i) % count` using `sdiv` + `msub`:
```arm64
sdiv    w1, w2, w21       ; integer divide
msub    w2, w1, w21, w2   ; modulo remainder
```
ARM64 integer divide is ~4-7 cycles on Apple M-series. This runs for every worker on every steal attempt.

**Fix:** Since `start ∈ [0, count)` and `i ∈ [0, count)`, the sum wraps at most once:
```csharp
int t = start + i;
if ((uint)t >= (uint)count) t -= count;
```
This replaces `sdiv`+`msub` with a single `cmp`+conditional `sub`.

**Impact:** HIGH — removes integer divide from the steal loop (runs on every idle worker, every iteration).

#### 3. Eliminate bounds checks in BoundedLocalQueue ring buffer indexing
**Problem:** After `uxtb` (byte mask to 0-255), the JIT still emits `cmp #256` + `bhs RNGCHKFAIL`:
```arm64
uxtb    w5, w3            ; index = real & 0xFF (always 0-255)
cmp     w5, #256          ; REDUNDANT: uxtb guarantees < 256
bhs     RNGCHKFAIL
ldr     x4, [x4, w5, UXTW #3]
```
This appears in TryPop (hot path for every job pop) and TryStealHalf.

**Fix:** Use `Unsafe.Add` with `MemoryMarshal.GetReference`:
```csharp
Debug.Assert((uint)index < (uint)QueueCapacity);
ref var slot = ref Unsafe.Add(ref MemoryMarshal.GetReference((Span<T?>)_buffer), index);
```

**Impact:** HIGH — removes branch from every TryPop (called for every job execution) and TryStealHalf.

#### 4. Replace ConcurrentStack<int> sleeper stack with custom lock-free stack
**Problem:** `TryWakeOneWorker` uses `ConcurrentStack<int>.TryPop`, which inlines to a heavy CAS loop with `CompareExchangeObject` on object references (box/unbox of `int`, GC tracking). The codegen shows multiple helper calls and spills.

**Fix:** Replace with a custom `Treiber stack` using `Interlocked.CompareExchange` on a packed int (or a small pre-allocated array with a lock-free index), avoiding all GC overhead for `int` values.

**Impact:** HIGH on DAGs with frequent wake events (every phase boundary triggers wakes).

---

### MEDIUM IMPACT

#### 5. Split PushToRingBuffer overflow path into cold helper
**Problem:** `PushToRingBuffer` has a huge frame (`stp … #0x90`, 956 bytes total) because the overflow path (128-iteration inject loop + Enqueue calls) is interleaved with the hot "store to ring + advance tail" path.

**Fix:** Move the overflow CAS + inject loop into `[MethodImpl(NoInlining)] PushToRingBufferOverflow(...)`, keeping only the fast path in the main method. This shrinks the hot method's frame and improves I-cache density.

**Impact:** MEDIUM — reduces prologue/epilogue and I-cache pressure for the common push path.

#### 6. Reorder TryExecuteOne branch layout for hot path fall-through
**Problem:** After `TryPop`, successful pop (`job != null`) takes a forward `cbnz` branch, while the failure path falls through. On many ARM64 cores, forward branches default to "not taken" prediction.

**Fix:** Restructure so successful pop is the fall-through path:
```csharp
var job = context.Deque.TryPop();
if (job == null)
    goto trySteal;
// hot path: execute job
```

**Impact:** MEDIUM — reduces branch mispredictions when local queue is usually non-empty.

#### 7. Custom coordinator spin backoff (replace SpinWait)
**Problem:** `RunUntilComplete` uses `SpinWait.SpinOnce(sleep1Threshold: -1)` which generates multiple helper calls on the idle path. The coordinator only needs: (1) a few CPU spins, (2) `Thread.Yield()`, never `Sleep`.

**Fix:** Hand-roll a minimal spin: counted `Thread.SpinWait` + `Thread.Yield()` without the SpinWait bookkeeping.

**Impact:** MEDIUM — lighter idle path for the coordinator when waiting for workers.

#### 8. Enable Dynamic PGO
**Problem:** All methods show "No PGO data" — the JIT is making layout/inlining decisions without profile guidance.

**Fix:** Enable Dynamic PGO (`DOTNET_TieredPGO=1`) and verify it improves branch layout and devirtualization.

**Impact:** MEDIUM — can improve branch prediction layout, enable guarded devirtualization for `Job.Execute`, etc.

---

### LOW IMPACT

#### 9. Remove Instrument field branches in benchmark builds
**Problem:** Every job checks `if (Instrument)` (`ldrb` + `cbz`) even when `Instrument == false`. This is 2 instructions x 530 jobs/tick.

**Fix:** Use `#if INSTRUMENT` or a const field to compile out the branch entirely in benchmark builds.

**Impact:** LOW — 2 instructions per job, well-predicted branch.

#### 10. [SkipLocalsInit] on hot methods
**Problem:** Several methods zero-init stack slots on entry (`str xzr, [fp, ...]`).

**Fix:** Add `[SkipLocalsInit]` to methods where all locals are definitely assigned before read.

**Impact:** LOW — saves a few stores per method call.

#### 11. SnapshotCollectorJob: hoist Sources.Length
**Problem:** `Sources.Length` is re-read from the array object many times in the unrolled `if (Sources.Length > N)` pattern.

**Fix:** Cache `var n = Sources.Length;` at method entry.

**Impact:** LOW — only 1 invocation per tick.

#### 12. Investigate shared generic codegen overhead
**Problem:** Disassembly shows `System.__Canon` (shared generic) instantiations, which may have extra indirection compared to concrete `T` specialization.

**Fix:** Measure concrete vs shared generic codegen; consider specialized paths for `Job` type.

**Impact:** LOW — unclear without measurement.

---

### NOT ACTIONABLE (Verified Correct / Runtime-Level)

- **GC write barriers** (`CORINFO_HELP_CHECKED_ASSIGN_REF` / `ASSIGN_REF`): Required for managed reference stores. Cannot safely remove.
- **Memory ordering** (`ldapr`/`stlur`/`casal`): All verified necessary for cross-thread correctness. Do not weaken.
- **`casal` vs `ldaxr`/`stlxr`**: Runtime/JIT choice, not controllable from C#.
- **`Interlocked` fence strength**: Full fence is the .NET memory model contract. Relaxing requires formal proof and is not exposed via C# APIs.
- **Virtual dispatch for Job.Execute**: Cannot be eliminated without changing the Job type hierarchy to use generics, function pointers, or PGO-guided devirtualization.
- **`Interlocked.Exchange` for LIFO slot**: JIT intrinsic, already optimal codegen path.

---

## Implementation Priority

| Priority | Item | Expected Effort |
|----------|------|----------------|
| 1 | Bounds check elimination in ComputeJob/SnapshotJob (#1) | Small — localized change |
| 2 | Integer division removal in TryStealAndExecute (#2) | Small — single expression change |
| 3 | Bounds check elimination in BoundedLocalQueue (#3) | Medium — multiple methods |
| 4 | Custom sleeper stack (#4) | Medium — new data structure |
| 5 | PushToRingBuffer cold split (#5) | Small — method extraction |
| 6 | TryExecuteOne branch reorder (#6) | Small — control flow change |
| 7 | Enable Dynamic PGO (#8) | Small — env var / project config |
| 8 | Custom coordinator spin (#7) | Medium — replace SpinWait |
