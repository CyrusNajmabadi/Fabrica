# Research: Architecture Validation — Industry Comparison

*Conducted ~2026-04-01. Web research agent surveyed 10+ topic areas to validate Fabrica's pipeline
architecture against industry practice.*

---

## Summary

Fabrica's core architecture was found to be **well-aligned with industry practice** across all
surveyed dimensions.

---

## Validated Design Decisions

### Fixed-Timestep Production + Variable-Rate Consumption

- **Textbook approach** grounded in Glenn Fiedler's "Fix Your Timestep" (Gaffer on Games).
- Used by: Unreal (game thread + render thread, "one frame behind"), Source engine (server
  snapshots at tickrate), Overwatch (ECS + fixed command frames).

### Immutable Snapshot Handoff via SPSC Queue

- **Volatile publish/acquire** for snapshot handoff is the standard SPSC idiom, correct on both
  x64 (TSO) and ARM64 under the CLR memory model.
- Maps directly to the Lamport SPSC queue pattern.

### Epoch-Based Reclamation with Pinning

- Maps to **EBR + hazard-pointer hybrids** from academic literature (Fraser/Harris, Linux RCU).
- Hard ceiling + exponential backpressure is consistent with Reactive Streams and Kafka/Flink
  lag-based throttling strategies.

### Struct-Constrained Generics for JIT Devirtualization

- Documented high-perf .NET pattern. CoreCLR PRs show explicit boxing removal work for
  `where T : struct, IInterface` patterns.
- Eliminates virtual dispatch and heap allocation in hot paths.

### Single-Threaded Pools

- Correct when ownership is exclusive. Avoids interlocked overhead.
- Validated by Unity's `Allocator.Temp` (per-thread, no cross-thread access).

### Dedicated Threads (Not ThreadPool) for Sim/Render

- What latency-sensitive engines do.
- Naughty Dog pinned 6 workers to cores for their fiber system.

---

## Identified Areas for Future Investigation

1. **PID controller for backpressure**: Could reduce oscillation ("hunting") between heavy
   throttle and no throttle. Streaming systems use credit-based flow control.

2. **ManualResetEventSlim vs AutoResetEvent**: Benchmarks show MRES can be faster for short
   waits due to spin-then-block behavior.

3. **LOH/Gen2 profiling**: If WorldImage grows large, pool misses trigger GC-heavy allocations.
   ArrayPool-style trimming under memory pressure could help.

4. **Persistent/COW data structures for WorldImage**: If snapshots share most state between
   ticks, structural sharing could dramatically reduce per-tick copy cost.

5. **Determinism under parallel workers**: Parallel sim workers must join in deterministic order
   to maintain reproducibility. Float nondeterminism across platforms is "never free."

---

## Warnings from Research

- **EBR stall risk**: One misbehaving deferred consumer can block all reclamation. Hard ceiling
  mitigates this, but formal documentation of the invariant is recommended.
- **Over-pinning CPU affinity** can hurt OS scheduling and throughput. Best-effort is correct.
- **Floating-point determinism** requires explicit policy — never assume it's free.
- **`volatile` alone doesn't fix multi-field invariants** — must publish one coherent view
  (single-pointer publish is correct).

---

## Top Sources

- **Fix Your Timestep** (Gaffer on Games) — canonical reference for fixed timestep
- **C# Memory Model in Theory and Practice** (MSDN Magazine 2012) — foundational for volatile
- **Fraser/Harris** — "Concurrent programming without locks" for EBR theory
- **Linux RCU memory ordering** (kernel.org) — deepest treatment of epoch-style reclamation
- **Reactive Streams spec** — formalized backpressure semantics
- **Naughty Dog GDC 2015** — fiber parallelism and core pinning in AAA engine
- **Overwatch GDC** — ECS + netcode architecture with fixed command frames
