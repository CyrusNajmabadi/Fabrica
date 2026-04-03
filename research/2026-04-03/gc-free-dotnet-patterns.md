# Research: GC-Free .NET Patterns

*Conducted 2026-04-03. How to achieve zero GC collections during steady-state simulation.*

---

## What Triggers GC

- **Gen0**: Allocated objects exceed adaptive threshold (self-tuning, not a fixed budget).
- **Gen1/Gen2**: Promoted objects exceed thresholds; Gen2 = full GC (all generations + LOH).
- **LOH**: Objects ≥ 85,000 bytes; collected with Gen2.
- `GC.Collect()` (avoid in production).

**Critical**: Gen0/Gen1 collections are **stop-the-world foreground** events. Even during
background Gen2, ephemeral collections **suspend all managed threads**. "Gen0 is cheap" is
misleading for deterministic simulation — design for **zero allocations in the tick loop**.

**Source**: Microsoft, *Fundamentals of garbage collection* (learn.microsoft.com)

---

## Server GC vs Workstation GC

- **Workstation**: Collection on triggering user thread, normal priority.
- **Server**: One heap + GC thread per logical CPU, high priority, larger segments.
- Server GC can be resource-heavy if many processes use it.

---

## Key Patterns for Zero Allocation

### 1. Struct-Based Jobs with Generic Constraints

```csharp
void Execute<TJob>(TJob job) where TJob : struct, IJob
```

- `where T : struct, IMyInterface` → JIT specializes per struct type.
- `.constrained` + `callvirt` IL avoids boxing.
- **No heap allocation** for the job itself.
- **Caveat**: JIT generates code per struct → larger code size. Not "free" but zero-alloc.

### 2. ArrayPool<T>

- Returns arrays from power-of-two bucketing.
- Multi-tier pool: thread-local fast path + shared tiers.
- **Gotcha**: `MemoryPool<T>.Shared.Rent()` allocates an `IMemoryOwner<T>` wrapper each time.
  Prefer `ArrayPool<T>` directly for GC-free paths.

### 3. stackalloc + Span<T>

- Stack allocation for short-lived buffers. Use with `Span<T>` in safe code.
- **Limit**: Bounded by thread stack size (~1 MB typical default).
- **Cannot use in async methods** — `ref struct` restrictions.

### 4. NativeMemory for Arena/Bump Allocators

```csharp
void* ptr = NativeMemory.Alloc(size);
// ... use via Unsafe.Add / pointer arithmetic ...
NativeMemory.Free(ptr);
```

- Completely bypasses GC heap.
- Build arena: Alloc big slab, bump pointer, reset each frame.
- **You own lifetime, alignment, aliasing, use-after-free**.

### 5. ref struct (Stack-Only Types)

- Cannot be array elements, cannot box, restricted in async/lambdas.
- `Span<T>`, `ReadOnlySpan<T>` are ref structs.
- C# 13: ref structs may implement interfaces but **cannot convert to the interface** (would box).

### 6. String Avoidance

- Never format strings in hot path. Use integer codes/enums.
- `ReadOnlySpan<char>` to slice strings without copying.
- UTF-8 spans for IO-heavy paths.
- Interpolated string handlers (C# 10+) can write into pooled buffers.

### 7. GC.TryStartNoGCRegion

- Tell the runtime "don't GC for a while."
- **Constraints**: Cannot nest. Memory must be pre-committed. May trigger full blocking GC to
  make space. Write barrier cost remains.
- Use case: Short critical sections with known allocation volume. Not a substitute for
  allocation-free design.

**Source**: Maoni Stephens, *No GCs for your allocations?* (devblogs.microsoft.com/dotnet)

---

## Profiling Tools

| Tool | Purpose |
|------|---------|
| `dotnet-counters` | Monitor GC heap size, collection counts in real-time |
| `dotnet-trace` | Collect EventPipe traces (cross-platform) |
| PerfView / ETW | Deep GC + CPU analysis (Windows) |
| `dotnet-gcdump` | Heap snapshots for leak investigation |
| BenchmarkDotNet `MemoryDiagnoser` | Per-benchmark Gen0/Gen1/Gen2 + allocated bytes |
| GC runtime events | `GCStart_V2`, `GCEnd_V1`, `GCHeapStats_V2` with reason codes |

### What to Watch For

- `[SkipLocalsInit]` attribute suppresses `.locals init` — unsafe if reading uninitialized memory.
- LINQ in hot paths: allocates enumerators.
- `foreach` over `IEnumerable<T>`: may box struct enumerators.
- Lambda captures: allocate closure objects.
- String interpolation: may allocate via `String.Format` depending on target type.

---

## Real-World GC-Free .NET Systems

| System | Approach |
|--------|----------|
| Unity Burst | AOT native subset of C#. No managed objects in Burst code. `NativeArray<T>`. |
| Stride Engine | Standard .NET. Entities are GC-managed. No official zero-GC claim. |
| Garnet (Microsoft) | Ongoing allocation reductions (PRs #1100, #1103, #1404). Lower allocs, not zero. |

**True "0 collections/sec forever" on CoreCLR** is rare in full-featured engines. The practical
target is **"0 allocations in the sim/render tick"** plus rare Gen2 from asset reloads/logging.

---

## Fabrica Application

### Hot Path (Per-Tick Simulation Loop)

- Jobs as `struct` with `where TJob : struct, IAllocator<TJob>` (already have this pattern).
- Job data in pre-allocated arrays or pooled objects (already have `JobPool`).
- Per-thread SPSC buffers for deferred operations (no LINQ, no lambdas, no string ops).
- `ArrayPool<T>` for any temporary buffers.

### Cold Path (Between Ticks / Cleanup)

- String formatting for logging/diagnostics is fine here.
- GC collections triggered by cold-path allocations won't interrupt the hot path if timed to
  inter-tick windows.

### Validation Strategy

- Add `dotnet-counters` monitoring to CI stress tests.
- `BenchmarkDotNet` + `MemoryDiagnoser` for per-operation allocation tracking.
- Goal: `Alloc/Op = 0 B` for all tick-loop operations.
