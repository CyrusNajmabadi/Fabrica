# Design Research — Prior Art and Best Practices

Web research into the patterns and strategies used by the Fabrica engine,
covering industry practice, academic literature, and runtime-specific guidance.

---

## 1. Decoupled Production/Consumption Pipelines

### Industry Practice

Separating fixed-timestep simulation from variable-rate rendering is the standard
approach in game engines:

- **Glenn Fiedler's "Fix Your Timestep"** remains the canonical reference: accumulate
  wall time, consume in fixed `dt` steps, interpolate for rendering.
- **Unreal Engine** uses Game Thread → Render Thread → RHI, typically one frame behind,
  with scene proxies to avoid sharing mutable actor state.
- **Unity** uses a Job System with safety via copies and work-stealing, plus configurable
  render threading modes.
- **id Tech 7** uses a job-centric model with no single "main thread."
- **Naughty Dog** uses a fiber-based system with workers pinned to cores.

### How Fabrica Compares

The 40 Hz producer / ~60 Hz consumer split matches the textbook "sim slower than render"
case. Publishing immutable snapshots via volatile pointer swap is in the same family as
Unreal's proxy split, but closer to functional state handoff.

### Sources

- [Fix Your Timestep — Gaffer on Games](https://gafferongames.com/post/fix_your_timestep)
- [Unreal Engine — Threaded Rendering](https://dev.epicgames.com/documentation/en-us/unreal-engine/threaded-rendering-in-unreal-engine)
- [Unity — Job System Overview](https://docs.unity3d.com/Manual/job-system-overview.html)
- [GDC Vault — Parallelizing the Naughty Dog Engine](https://gdcvault.com/play/1022186/Parallelizing-the-Naughty-Dog-Engine-Using-Fibers)

---

## 2. Immutable Snapshots for Game State

### Industry Practice

Networking stacks universally deal in snapshots — Valve's Source engine uses server
snapshots at tickrate with client interpolation. Unity Netcode for Entities uses "ghost
snapshots." Overwatch uses ECS with fixed command frames and prediction/rollback.

The "immutable forward chain" in Fabrica is closer to versioned state for local
readers (render/save) than to wire-format network snapshots, but the memory philosophy
(publish coherent views) is analogous.

### Memory Strategies

- **Structural sharing** / **persistent data structures** reduce naive O(n) copy per tick.
- **Copy-on-write** strategies let snapshots share unchanged subtrees.
- **Delta compression** is standard for network replication.

### Sources

- [GDC Vault — Overwatch Gameplay Architecture](https://gdcvault.com/play/1024001)
- [Unity — Ghost Snapshots](https://docs.unity3d.com/Packages/com.unity.netcode@1.0/manual/ghost-snapshots.html)
- [Source Multiplayer Networking (archived)](https://web.archive.org/web/20200910022723/developer.valvesoftware.com/wiki/Source_Multiplayer_Networking)
- [Persistent Data Structures — Wikipedia](https://en.wikipedia.org/wiki/Persistent_data_structure)

---

## 3. Lock-Free Communication in .NET

### Key Findings

- C# `volatile` provides acquire/release semantics. The definitive reference is
  "The C# Memory Model in Theory and Practice" (MSDN Magazine, Dec 2012).
- On **ARM64** (Apple Silicon), the weak baseline ordering means barriers are essential.
  .NET codegen emits appropriate atomics/barriers for `volatile` and `Interlocked`.
- On **x64** (TSO), the hardware is more forgiving, but correct C# patterns must still
  be expressed — the implementation handles the rest.
- **SPSC** (single-producer single-consumer) is the formal name for the Fabrica pattern.
  Lamport queues are the classic academic formalization.
- `Interlocked` operations are for read-modify-write; `volatile` is sufficient for
  single-word loads/stores with ordering.

### Relevance

Publishing a single `ChainNode` pointer with release semantics and acquiring on read is
the standard SPSC idiom. All data written before the volatile publish is visible after
the acquire read.

### Sources

- [C# Memory Model — Theory and Practice](https://learn.microsoft.com/en-us/archive/msdn-magazine/2012/december/csharp-the-csharp-memory-model-in-theory-and-practice)
- [Jon Skeet — Volatility](https://www.jonskeet.uk/csharp/threads/volatility.html)
- [ARM64 Memory Barriers — Kunal Pathak](https://kunalspathak.github.io/2020-07-25-ARM64-Memory-Barriers/)

---

## 4. Epoch-Based Memory Reclamation

### Key Findings

- **EBR** was popularized by Fraser and Harris's work on lock-free data structures.
  Objects become reclaimable only after all threads have moved past an epoch.
- **Known weakness:** A stalled thread can prevent epoch advance, causing unbounded
  unreclaimed memory. Mitigations include interval-based reclamation (IBR), wait-free
  eras, and hybrid schemes.
- **Linux RCU** (Read-Copy-Update) is the most widely deployed epoch-style system,
  with extensive documentation of ordering requirements.
- **Hazard pointers** offer bounded stranded memory but have scan cost and scalability
  concerns.
- In **GC'd runtimes** like .NET, epoch-based pooling is about reuse and deterministic
  cost, not preventing use-after-free.

### Relevance

Fabrica's `ConsumptionEpoch` + `PinnedVersions` maps directly to EBR + hazard-style
pinning. The hard ceiling backpressure prevents the unbounded growth that EBR literature
warns about.

### Sources

- [Fraser/Harris — Concurrent Programming Without Locks](https://www.semanticscholar.org/paper/Concurrent-programming-without-locks-Fraser-Harris/6b6e4bc51aa3dccb44a4fe3b384774856aab36)
- [Linux RCU Memory Ordering Tour](https://www.kernel.org/doc/html/latest/RCU/Design/Memory-Ordering/Tree-RCU-Memory-Ordering.html)
- [Epoch Protection Paper (PDF)](https://tli2.github.io/assets/pdf/epochs.pdf)

---

## 5. Object Pooling in .NET

### Key Findings

- **Single-threaded pools** are cheaper than thread-safe variants (no atomics).
  Appropriate when ownership is exclusive — matches Fabrica's producer-owned pools.
- **Stack-based (LIFO)** pools maximize recency and cache warmth.
- **LOH threshold** (85KB+) causes expensive Gen2 collections. Pooling large objects
  is critical for avoiding this.
- `ArrayPool<T>` and `Microsoft.Extensions.ObjectPool` are the standard .NET APIs.
  Custom pools are justified when you need single-threaded guarantees or specific
  reset semantics.

### Relevance

Fabrica's `ObjectPool<T, TAllocator>` is well-aligned. Profile LOH and Gen2 as
`WorldImage` grows. Consider bounded pool sizing with trimming under memory pressure.

### Sources

- [ObjectPool — ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/objectpool)
- [ArrayPool — Adam Sitnik](https://adamsitnik.com/Array-Pool/)
- [LOH Deep Dive](https://medium.com/@damithw/deep-dive-into-net-large-object-heap-loh-and-practical-use-of-arraypool-t-for-high-performance-22e244138e8d)

---

## 6. Back-Pressure and Throttling

### Key Findings

- **Games:** Fiedler's accumulator pattern can cause a "spiral of death" if sim
  can't keep up — engines clamp sub-steps, drop, or slow the producer.
- **Frame pacing:** Consistency of present times matters more than average FPS.
- **Streaming systems:** Reactive Streams formalized non-blocking backpressure via
  subscription/credits. Kafka uses consumer lag metrics. Flink uses credit-based
  network flow control.
- **Controllers:** PID controllers, token buckets, and AIMD are common. Exponential
  backoff is standard for contention/retry.

### Relevance

Fabrica's epoch-gap → exponential delay with a hard ceiling parallels lag-based
throttling in stream processors and acts as a circuit breaker against OOM.
Consider a PID controller if oscillation ("hunting") becomes an issue.

### Sources

- [Reactive Streams](https://www.reactive-streams.org/)
- [Flink — Backpressure](https://flink.apache.org/2019/07/23/flink-network-stack-vol.-2-monitoring-metrics-and-that-backpressure-thing)
- [Frame Timing vs Frame Pacing](https://pulsegeek.com/articles/frame-timing-vs-frame-pacing-stability-over-speed/)

---

## 7. Thread Affinity and Core Allocation

### Key Findings

- Thread pinning can improve cache locality and timing stability (5–20% wins reported
  in some cases), but can also hurt OS scheduling flexibility.
- **Naughty Dog** pinned 6 workers to cores for their fiber system.
- **NUMA awareness** matters on servers; client single-socket is less critical.
- Dedicated threads for latency-sensitive loops avoid ThreadPool injection delays.
- macOS uses QoS classes rather than hard affinity masks.

### Relevance

Fabrica's best-effort pinning on Windows/Linux is consistent with industry practice.
Note that both sim and render worker groups pin starting from core 0 — potential
contention if running simultaneously.

### Sources

- [GDC — Parallelizing Naughty Dog Engine](https://gdcvault.com/play/1022186/Parallelizing-the-Naughty-Dog-Engine-Using-Fibers)
- [SetThreadAffinityMask — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadaffinitymask)

---

## 8. Worker Thread Synchronization in .NET

### Key Findings

- `ManualResetEventSlim` is often much faster than `ManualResetEvent` / `AutoResetEvent`
  for short waits due to spin-then-block behavior.
- `SpinWait` is appropriate for very short critical sections; avoid burning CPU on
  long waits.
- `Parallel.For` with partitioning often beats naive manual pools but loses when you
  need deterministic ordering.
- `CancellationToken` is cooperative; `Cancel()` can be slow with many registrations.

### Relevance

Fabrica uses `AutoResetEvent` for worker wake. If wake latency is hot, benchmarking
`ManualResetEventSlim` with manual reset could help.

### Sources

- [ManualResetEventSlim — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.threading.manualreseteventslim)
- [SpinWait — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/threading/spinwait)
- [CancellationTokenSource partitioning — dotnet/runtime](https://github.com/dotnet/runtime/pull/48251)

---

## 9. Struct-Based Generic Specialization in .NET

### Key Findings

- `where T : struct, IInterface` enables JIT devirtualization and inlining per closed
  struct type. RyuJIT has explicit boxing removal for constrained struct interface calls.
- Pitfalls: `readonly struct` + `in` needed to avoid defensive copies. Mutable structs
  are error-prone.
- Similar to C++ templates and Rust monomorphization, but relies on JIT rather than
  compile-time specialization.
- Well-known pattern in high-performance .NET (e.g., `Span<T>` ecosystem).

### Relevance

Fabrica's struct-constrained hot interfaces are aligned with documented best practices.
Validate with BenchmarkDotNet on both x64 and ARM64.

### Sources

- [Avoiding Virtual Call Overhead — Stack Overflow](https://stackoverflow.com/questions/53785910/avoiding-the-overhead-of-c-virtual-calls)
- [CoreCLR — Struct Interface Boxing Removal](https://github.com/dotnet/coreclr/pull/17006)

---

## 10. Simulation Determinism

### Key Findings

- Fixed timestep + interpolation remains the standard. Newer work extends with
  integer time representations.
- Floating-point determinism across platforms is "never free" — requires explicit
  policy regarding FMA, math libs, SSE vs scalar.
- Parallel workers inside a sim tick must join in deterministic order for
  reproducibility.
- .NET GC nondeterminism in allocation timing rarely affects pure numeric simulation.

### Relevance

Fabrica's 40 Hz sim determinism is orthogonal to render rate if the sim only reads
committed inputs and uses fixed `dt`. Parallel workers need deterministic scheduling
or single-threaded sim with parallel islands.

### Sources

- [Fix Your Timestep — Gaffer on Games](https://gafferongames.com/post/fix_your_timestep)
- [Deterministic Physics — GameDev.SE](https://gamedev.stackexchange.com/questions/174320/how-can-i-perform-a-deterministic-physics-simulation)
- [bepu/bepuphysics2 — Cross-Machine Determinism](https://github.com/bepu/bepuphysics2/issues/94)

---

## Top Recommendations

Ordered by impact:

1. **Formalize the memory model contract** — document happens-before between writer
   thread fields and volatile chain publish. Consider `Volatile.Read/Write` or
   `Interlocked.Exchange` for auditability.
2. **Keep backpressure coupled with epoch reclamation** — the hard ceiling prevents
   the unbounded growth that EBR literature warns about.
3. **Expose two snapshots for interpolation** — Fiedler-style alpha between previous
   and current immutable states (already in place).
4. **Profile LOH and Gen2** — `WorldImage` size distribution drives GC pause risk.
5. **Benchmark struct-constrained interfaces** on ARM64 and x64 — validate JIT
   specialization assumptions.
6. **Instrument epoch gap, throttle depth, pool high-water** — Kafka/Flink-style lag
   metrics catch production issues early.
7. **Document pinning rules** — analogous to hazard pointers / RCU grace periods.
8. **Benchmark wake primitives** — `ManualResetEventSlim` vs `AutoResetEvent` under
   your actual dispatch pattern.
9. **Determinism checklist** — fixed `dt`, ordered inputs, no float nondeterminism
   unless audited.
10. **Consider PID-based throttling** if exponential backoff oscillation becomes an
    issue in practice.

## Warnings

- **EBR stall risk:** One misbehaving deferred consumer can block all reclamation
  without mitigation (the hard ceiling addresses this).
- **`volatile` alone does not fix multi-field invariants** — must publish one
  coherent view (which the single-pointer publish already does).
- **Over-pinning CPU affinity** can hurt OS scheduling and throughput.
- **Floating-point determinism across OS/CPU is never free.**
- **Object pooling misuse:** memory leaks, stale data, forgotten returns.
