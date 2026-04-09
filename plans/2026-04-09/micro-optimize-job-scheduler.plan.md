---
name: Micro-optimize job scheduler
overview: Add fine-grained instrumentation to identify per-operation scheduling costs, then apply targeted micro-optimizations to close the 99μs gap (13% overhead) between measured and theoretical optimal throughput.
todos:
  - id: instrument
    content: Add per-worker event recording to WorkerPool (pop source, idle gap, steal details, fan-out cost) behind an Instrument flag
    status: completed
  - id: analyze
    content: Extend RealisticTickBenchmark to consume worker event buffers and print thread timeline / idle breakdown
    status: completed
  - id: opt-wake
    content: "Proportional wake: wake min(readied, parked) workers in PropagateCompletion instead of just 1"
    status: cancelled
  - id: opt-fanout
    content: "Direct injection for large fan-outs: push some locally, inject remainder to shared queue"
    status: cancelled
  - id: opt-steal-copy
    content: Evaluate plain reads in TryStealHalf copy loop (replace Interlocked.Exchange with plain read+null)
    status: completed
  - id: opt-priority
    content: Evaluate reordering TryExecuteOne to check injection before stealing
    status: cancelled
  - id: benchmark
    content: Re-run RealisticTickBenchmark after each optimization and compare to 762μs baseline
    status: completed
isProject: false
---

# Micro-Optimize Job Scheduler

## Current State

- **Measured P50**: 762 μs (12 P-cores, ~530 jobs, 4 sequential phases)
- **Theoretical optimal**: 663 μs (total work / cores, phases divide evenly by 12)
- **Gap**: 99 μs (13%), split into ~17 μs inter-phase transitions + ~82 μs intra-phase parallelism loss
- **Worst phase**: P2 at 84.7% efficiency (44 μs lost), P4 at 81.6% (13.5 μs lost)

## Strategy: Profile First, Then Optimize

We already have **existing speedscope CPU sampling traces** from the benchmark runs at `/tmp/bench-newqueue/*.speedscope.json`. However, these are 1kHz sampling — too coarse for microsecond-level scheduling decisions. We need **custom sub-microsecond instrumentation** in the scheduler itself.

---

## Step 1: Add Scheduler-Level Instrumentation

Add an optional `Instrument` flag to `WorkerPool` (like the benchmark jobs already have). When enabled, record **per-job scheduling events** using `Stopwatch.GetTimestamp()` into a pre-allocated per-worker circular buffer. Events to capture:

**In [WorkerPool.cs](src/Fabrica.Core/Jobs/WorkerPool.cs):**

- **Pop source**: Was job obtained via local TryPop, TryStealHalf, or injection TryDequeue?
- **Idle gap**: Timestamp between "previous ExecuteJob returned" and "next job obtained" — this is the scheduling overhead per job
- **Steal details**: Which victim, how many items batch-stolen
- **Fan-out cost**: In `PropagateCompletion`, timestamp before/after the dependency loop + count of readied jobs
- **Wake cost**: In `TryWakeOneWorker`, whether a wake was actually issued

**In the benchmark ([RealisticTickBenchmark.cs](benchmarks/Fabrica.SampleGame.Benchmarks/Scale/RealisticTickBenchmark.cs)):**

- Extend `PrintAnalysis` to consume the per-worker event buffers and print:
  - Per-worker idle time breakdown (how much time each worker spent looking for work vs executing)
  - Fan-out latency per barrier (time from barrier completion to all workers having work)
  - Steal frequency and batch size distribution
  - Pop source distribution (local vs steal vs injection)

This gives us a **complete thread timeline** showing exactly where the 82 μs of intra-phase loss comes from.

---

## Step 2: Targeted Micro-Optimizations

Based on code analysis, these are the highest-probability wins (implement after instrumentation confirms):

### 2a. Proportional Wake in PropagateCompletion

**Current**: `PropagateCompletion` calls `TryWakeOneWorker()` **once** regardless of how many jobs were readied.

**Problem**: When a barrier fans out 192 jobs, only 1 parked worker is woken. The rest must discover work via their existing search loops (HotSpin/WarmYield). If workers are in WarmYield, there's a `Thread.Yield()` round-trip (~1-5 μs on macOS) before they poll again.

**Fix**: Wake min(readied, parked) workers — pass `readied` count and loop `TryWakeOneWorker`:

```csharp
if (readied > 0)
    for (var w = 0; w < readied; w++)
        if (!this.TryWakeOneWorker())
            break;
```

**File**: [WorkerPool.cs](src/Fabrica.Core/Jobs/WorkerPool.cs) lines 513-518

### 2b. Direct Injection for Large Fan-Outs

**Current**: `PropagateCompletion` pushes ALL readied dependents onto the local deque via `context.Deque.Push(dependent)`. With 192 readied jobs, one worker's queue holds everything. Other workers must steal via TryStealHalf (Phase 1 CAS + n Interlocked.Exchange + Phase 2 CAS per steal).

**Problem**: Stealing is O(log N) rounds to distribute across all cores. Each round involves cross-core atomics.

**Fix**: Push the first batch locally (LIFO, cache-hot), inject the remainder directly into the shared injection queue so all workers can grab them without cross-core CAS:

```csharp
var localBudget = Math.Min(readied, QueueCapacity / 2); // keep some local
// ... push first localBudget, inject the rest
```

**Files**: [WorkerPool.cs](src/Fabrica.Core/Jobs/WorkerPool.cs) `PropagateCompletion`

### 2c. Plain Reads in TryStealHalf Copy Loop

**Current**: Each stolen slot uses `Interlocked.Exchange(ref _buffer[srcIdx], null)` — a full atomic swap per item.

**Why it might be safe to use plain reads**: After the Phase 1 CAS, the range `[steal, steal+n)` is exclusively owned by the thief. The owner can't write to those slots (`steal != real` forces overflow path). No other thief can start (early exit when `steal != real`). The Phase 1 CAS provides a full fence guaranteeing visibility of prior writes.

**Fix**: Replace with plain read + plain null, relying on the Phase 1 CAS fence:

```csharp
destination._buffer[dstIdx] = _buffer[srcIdx]!;
_buffer[srcIdx] = null;
```

**Risk**: ARM64 JIT reordering — needs careful verification. This optimization should be validated with stress tests on ARM64 and ideally a sub-agent review against Tokio's equivalent.

**File**: [BoundedLocalQueue.cs](src/Fabrica.Core/Threading/Queues/BoundedLocalQueue.cs) lines 378-387

### 2d. Reorder TryExecuteOne Priority

**Current priority**: (1) local TryPop, (2) steal from peers, (3) injection queue.

**Observation**: After a barrier fan-out that overflows the local queue, stolen items go to the injection queue. But workers try stealing (expensive cross-core CAS) before checking injection (cheap lock-based dequeue). If injection has items, checking it earlier avoids unnecessary steal attempts.

**Possible fix**: Try injection before stealing, or interleave: `local → injection → steal`.

**Tradeoff**: Injection items are "cold" (not cache-local). Steal items from a peer's queue are "warmer" (recently pushed by a nearby core). Need to benchmark both orderings.

**File**: [WorkerPool.cs](src/Fabrica.Core/Jobs/WorkerPool.cs) `TryExecuteOne` lines 415-429

---

## Step 3: Benchmark and Validate

After each optimization, re-run `RealisticTickBenchmark` with `WorkerCountOverride=0` and compare against the current 762 μs baseline. Run the enhanced instrumentation to verify the optimization hit the right target.

---

## Existing Profiling Assets

- **Speedscope traces**: `/tmp/bench-newqueue/*.speedscope.json` — CPU sampling from BenchmarkDotNet's `[EventPipeProfiler]`. Open at speedscope.dev for flamegraphs.
- **Phase analysis**: Already built into `RealisticTickBenchmark.GlobalCleanup` — 2000-tick analysis with percentile tables.
- **JIT disasm**: Available via `DOTNET_JitDisasm=*MethodName`* — useful for verifying inlining and bounds check elimination on hot methods.

