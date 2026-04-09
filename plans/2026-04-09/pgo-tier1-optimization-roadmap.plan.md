# PGO Tier1 Optimization Roadmap

Platform: .NET 10, ARM64, Apple M4 Max
Assembly captured with: Dynamic PGO + Tiered Compilation (Tier1 final code)
Date: 2026-04-09

## Methodology

- Each optimization is a **separate PR** off `master`.
- If accepted, merge to master before starting the next.
- Each PR is evaluated on **two axes**:
  1. **Disassembly verification**: Did the JIT output change as intended?
  2. **Benchmark performance**: Did wall-clock time improve?
- A change may be kept even if benchmarks are flat, if the disassembly confirms the
  intended improvement (the path may not be hot enough to measure, but the fix is correct
  and beneficial for other workloads).
- Baseline benchmark: `RealisticTickBenchmark`, P-core only.
- Disassembly captured via `tools/JitDisasm` harness with `DOTNET_JitDisasmOnlyOptimized=1`
  and `DOTNET_JitDisasmSummary=1` (200 ticks for full Tier1 promotion).

---

## Optimizations (Ranked by Expected Impact)

### 1. Batch InjectionQueue.Enqueue under one lock

**Problem**: The overflow path in `Push`/`PushToRingBuffer` inlines 3 separate `Enqueue`
calls, each with its own `Lock.Enter`/`Lock.Exit` + finally funclet. This bloats each
method to ~1KB. The same pattern duplicates into `ExecuteJob` (1,832 bytes) and
`PropagateCompletion` (1,240 bytes) via PGO inlining.

**Fix**: Add `EnqueueBatch(ReadOnlySpan<T>)` or similar API to `InjectionQueue<T>` that
takes the lock once and pushes all items. Route the overflow path through it. Optionally
mark the overflow helper `[NoInlining]` to keep the hot ring-write path compact.

**Verify (disasm)**: `Push`/`PushToRingBuffer` should shrink significantly (target <500
bytes each). Only one lock acquire/release sequence in the overflow path. `ExecuteJob` and
`PropagateCompletion` should also shrink if they were inlining the old Enqueue.

**Verify (perf)**: Benchmark before/after. I-cache improvement may or may not show on this
specific benchmark depending on overflow frequency.

**Files**: `InjectionQueue.cs`, `BoundedLocalQueue.cs`

---

### 2. Hoist stable pointers in unrolled BenchNodeOps methods

**Problem**: `EnumerateChildren` (1,820 bytes, 48K calls/tick) and
`IncrementChildRefCounts` (644 bytes, 44K calls/tick) re-load the same
`GlobalNodeStore → RefCountTable → SlabDirectory` pointer chain for every child slot
(9 slots × ~8 dependent loads = ~72 redundant loads per call). The JIT does not hoist
these across the unrolled copies.

**Fix**: At the start of each method, cache `Store`, `RefCountTable`, and the slab
directory base pointer in locals. Pass these into the per-slot logic so each iteration
only does handle-dependent work (shift/mask/index).

**Verify (disasm)**: The repeated `ldr x, [x, #0x30] → ldr x, [x, #0x08] → ldp w, w, [x, #0x14]`
chain should appear once at method entry, not 9× in the unrolled body.

**Verify (perf)**: ~72 fewer dependent loads per call × 48K calls/tick = ~3.5M fewer loads
per tick. Should be measurable.

**Files**: `BenchNodeOps` (in `Fabrica.SampleGame`), possibly `INodeOps<T>` interface

---

### 3. Flatten RemapTable storage

**Problem**: `RemapTable` uses `UnsafeList<int>[]` (jagged array of lists). Each
`Remap` call (418K/tick) chases: outer array → list object → backing `int[]` → element.
Multiple pointer indirections per call.

**Fix**: Replace with a flat `int[]` with per-thread base offsets (prefix sums computed
during `DrainBuffers`). Remap becomes `flat[base[threadId] + localIndex]` — one array
load after decoding.

**Verify (disasm)**: `Remap` should show fewer `ldr` chains — ideally 2-3 dependent loads
instead of 5+.

**Verify (perf)**: 418K fewer pointer chases per tick. Should be measurable on the merge
phase.

**Files**: `RemapTable.cs`, `GlobalNodeStore.cs`

---

### 4. Batch IncrementOutstanding in PropagateCompletion

**Problem**: Each readied dependent does a separate `Interlocked.Increment` on
`_outstandingJobs` (`ldaddal` instruction). When a job has N>1 dependents that all become
ready, this is N atomic RMW operations.

**Fix**: After the dependents loop, do a single `Interlocked.Add(ref _outstandingJobs, readied)`
instead of N separate increments.

**Verify (disasm)**: `PropagateCompletion` should show one `ldaddal` for outstanding count
instead of one per dependent.

**Verify (perf)**: Reduces atomic contention on `_outstandingJobs`. Improvement depends on
average fan-out.

**Files**: `WorkerPool.cs`

---

### 5. Shrink ExecuteJob via [NoInlining] on cold paths

**Problem**: PGO inlined `ComputeJob.Execute` (the entire hash loop), full
`PropagateCompletion` (including overflow), and instrumentation paths into `ExecuteJob`,
making it 1,832 bytes. This is excessive I-cache pressure for a per-job dispatch method.

**Fix**: Mark `PropagateCompletion` as `[NoInlining]`. Consider the same for the
instrumentation recording block. The hot path (type check → inlined ComputeJob → return)
stays fast; cold paths become calls.

**Verify (disasm)**: `ExecuteJob` should shrink to <800 bytes. `PropagateCompletion` stays
its own Tier1 method.

**Verify (perf)**: I-cache improvement. May be subtle on this benchmark.

**Files**: `WorkerPool.cs`

---

### 6. Reduce EnumerateChildren code size

**Problem**: `EnumerateChildren` is 1,820 bytes for 9 child slots — the JIT fully unrolled
9 iterations of decrement-refcount + conditional free-list push. At 48K calls/tick, this
occupies a significant portion of L1 I-cache.

**Fix**: Extract the "decrement refcount, if zero → push to free list → maybe cascade"
logic into a single `[NoInlining]` static helper. Keep the 9-slot check-and-dispatch
inline but call the helper for the actual work.

**Verify (disasm)**: `EnumerateChildren` should shrink to <400 bytes (9 × tbnz/branch +
helper call).

**Verify (perf)**: I-cache improvement. The helper call overhead (~5ns) vs reduced
I-cache misses — net effect depends on working set.

**Files**: `BenchNodeOps` (in `Fabrica.SampleGame`), possibly `GlobalNodeStore.cs`

---

### 7. Use uint indices in ComputeJob/SnapshotJob hash loops

**Problem**: The hash loop index uses `sbfiz x7, x7, #2, #32` (sign-extend + shift)
for array element addressing. Since `idx = (i + seed) & mask` is always non-negative,
using `uint` could produce `ubfiz` or simpler addressing.

**Fix**: Change loop variable `i`, `idx`, `seed` to `uint` in the hash loop.

**Verify (disasm)**: `sbfiz` should become `ubfiz` or `lsl` + `add` without sign
extension.

**Verify (perf)**: Micro — single instruction change in tight loop. 12M iterations/tick.

**Files**: `ComputeJob.cs`, `SnapshotJob.cs`

---

### 8. Eliminate per-node zero-fill in ThreadLocalBuffer.Allocate

**Problem**: Each `Allocate` call zero-initializes a full 40-byte `BenchNode` via
`stp xzr, xzr` × 2 + `str xzr`, even though the caller immediately overwrites the node.

**Fix**: Add an `AllocateUninitialized` path (or skip zero-init when the caller guarantees
full overwrite). Use `[SkipLocalsInit]` or `Unsafe.SkipInit` on the slot.

**Verify (disasm)**: The `stp xzr` sequence should disappear from the allocate path.

**Verify (perf)**: ~5 stores × 48K calls = 240K fewer stores/tick.

**Files**: `ThreadLocalBuffer.cs`, `UnsafeSlabArena.cs`

---

### 9. Pre-size cascade/free-list stacks

**Problem**: When refcount hits zero, `DecrementRefCount` pushes to the free list via
`UnsafeList.Add`. The Tier1 code includes an `EnsureCapacity` resize path (cold but
inflates code size).

**Fix**: Pre-size `_cascadePending` and arena free-list backing lists using peak
cascade depth or high-water-mark heuristics so the resize helper never fires in steady
state.

**Verify (disasm)**: The resize/`EnsureCapacity` helper call should still exist but the
branch predictor should never take it (confirm with PGO counts).

**Verify (perf)**: Primarily a code-size / I-cache improvement.

**Files**: `GlobalNodeStore.cs`, `UnsafeSlabArena.cs`

---

### 10. Convert UnsafeList<T> to a mutable struct

**Problem**: `UnsafeList<T>` is currently a class. Every type that holds one
(`UnsafeStack<T>`, `RefCountTable<T>`, `GlobalNodeStore` fields, etc.) pays an extra heap
allocation and an extra pointer dereference to reach the backing `T[]` and `_count`. On hot
paths that access list internals (e.g., `Push`/`Pop` on `UnsafeStack`, `Add`/`AddRange`),
this adds a dependent load in every call.

**Fix**: Convert `UnsafeList<T>` from a `class` to a `struct`. Unlike `UnsafeStack` and
`RefCountTable` (which became `readonly struct`), `UnsafeList` must be a plain `struct`
because it mutates `_count` and `_array`. This has cascading impacts:
- `UnsafeStack<T>` can no longer be `readonly struct` (it mutates its `_list` field).
- Other types holding `UnsafeList` fields directly (e.g., `_drainedHandles` in
  `GlobalNodeStore`) cannot mark those fields `readonly`.
- Pass-by-ref semantics must be used carefully to avoid accidental copies.

**Verify (disasm)**: Methods like `UnsafeStack.Push`/`Pop`, `UnsafeList.Add` should show
one fewer `ldr` in their pointer chain (no class header dereference). `RefCountTable`
operations that go through the slab directory should also benefit.

**Verify (perf)**: Eliminates heap allocations for every `UnsafeList` instance and removes
one pointer hop on every access. Should compound with optimizations #1 and #2.

**Files**: `UnsafeList.cs`, `UnsafeStack.cs`, `RefCountTable.cs`, `GlobalNodeStore.cs`,
`UnsafeSlabArena.cs`, `ProducerConsumerQueue.cs`, `DagValidator.cs`, and all test/benchmark
call sites.

---

### 11. Branchless handle operations via zero-sentinel index

**Problem**: Every `IncrementChildRefCounts`, `EnumerateChildren`, and `EnumerateRefChildren`
call checks `handle.IsValid` (or `handle != Handle.None`) before operating on each of the
9 child slots. That's 9 conditional branches per call × tens of thousands of calls per tick.
Branch mispredictions on partially-populated nodes are expensive, and the branches inflate
code size.

Currently `Handle<T>.None` uses index `-1`, and `IsValid` checks `Index >= 0`. This means
every child slot requires a test-and-branch before the actual work.

**Fix**: Change the sentinel from `-1` to `0`. `default(Handle<T>)` naturally produces
`Index = 0`, so uninitialized handles are automatically invalid. All valid handles start at
index 1. Tables (`RefCountTable`, `UnsafeSlabArena`, etc.) reserve slot 0 as a no-op
sink — increments/decrements to slot 0 are harmless and ignored.

This eliminates the `IsValid` check entirely from hot paths:

```csharp
// Before (9 branches):
if (node.Child0.IsValid) refCounts.Increment(node.Child0);
if (node.Child1.IsValid) refCounts.Increment(node.Child1);
...

// After (0 branches):
refCounts.Increment(node.Child0);
refCounts.Increment(node.Child1);
...
```

The 0-slot absorbs operations on invalid handles with no correctness impact.

**Verify (disasm)**: `IncrementChildRefCounts` should drop from 564 bytes to ~200 bytes
(9 straight-line increments, no `tbnz`/`cbz` branches). `EnumerateChildren` and
`EnumerateRefChildren` should see similar reductions.

**Verify (perf)**: Eliminates ~9 branches × 48K calls/tick = ~432K branches/tick. Should
be measurable from both branch prediction and I-cache improvement.

**Files**: `Handle.cs`, `RefCountTable.cs`, `UnsafeSlabArena.cs`, `BenchNodeOps.cs`,
`GlobalNodeStore.cs`, `ThreadLocalBuffer.cs`, and all code that checks `IsValid` or
compares against `Handle.None`.

**NOTE**: This is a large cross-cutting change. Do it last, after all other optimizations
are landed.

---

## Not Planned (Architectural / Future)

These were identified but require larger design changes. Documenting for future reference:

- **Job handles instead of object references in ring buffer**: Would eliminate all GC write
  barriers in queue operations, but requires a complete job identity model change.
- **Inline dependent list in Job**: Small-buffer optimization for `Dependents` to reduce
  allocations. Requires Job layout redesign.
- **JobContext as byref struct**: Passing `WorkerContext` by ref to `Execute` instead of
  wrapping in `new JobContext(context)`. API change across all job types.
- **BenchNode size 40→32 (power of 2)**: Would turn `smull` into shift, but requires
  dropping a child field or restructuring the node.
- **Flat slab directory**: Reduce 5-load pointer chain to reach slab elements. Major
  arena redesign.
- **Generic specialization to avoid System.__Canon**: Ensure hot queue instantiation uses
  concrete type. Requires queue type hierarchy changes.
