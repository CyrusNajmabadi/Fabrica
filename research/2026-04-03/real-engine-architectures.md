# Research: Real Engine Threading & Data Flow Architectures

*Conducted 2026-04-03. How production engines minimize cross-core synchronization.*

---

## Cross-Cutting Principle

Every production engine converges on: **partitioned data** (each job owns a slice), **dependency
graphs** (counters/DAGs for ordering), **frame-scoped arenas** (fewer allocator locks), and
**one-way pipelines** (sim → extract → render). Cores rarely share mutable cache lines.

---

## Naughty Dog (The Last of Us, Uncharted)

### GDC 2015: Fiber-Based Job System

- Worker threads pinned to cores. Pool of 160 fibers with small/large stacks.
- Multiple priority job queues. `run_jobs` + `wait_for_counter` API.
- Waiting does NOT block OS thread — fiber sleeps, another runs.
- Move from PS3 "jobs run to completion" to yielding jobs for nested dependencies.

### Data Sharing

- Fibers share address space like any worker thread.
- Parallelism requires disciplined data partitioning.
- 2017 follow-up talks covered allocation patterns and lock reduction.

**Source**: Gyrling, GDC 2015
[PDF](https://media.gdcvault.com/gdc2015/presentations/Gyrling_Christian_Parallelizing_The_Naughty.pdf)

---

## id Tech 7 (DOOM Eternal)

- **No classic "main thread"**. Everything is jobs.
- GPU-heavy pipeline: Vulkan, forward+, compute skinning, temporal techniques, bindless shading.
- GPU data flow is one-way with transient resources — no concurrent mutation of same object.
- **CPU-side world-state sharing model** is under-documented in open literature vs Naughty Dog.

**Source**: Coenen, *DOOM Eternal Graphics Study* (simoncoenen.com)

---

## Bungie (Destiny 2)

- Almost everything expressed as a **job graph** with limited preemption.
- **Resource tracking / validation layer** for multithreaded lifetimes.
- Machine-checked read/write/create/destroy rules within frame.
- Parallelism is safe when dependencies and lifetimes are validated, not just reviewed.

**Source**: Genova, *Multithreading the Entire Destiny Engine*, GDC 2015

---

## The Machinery (Tobias Persson)

### Job System

- Modeled on Naughty Dog. Everything after boot runs as jobs.
- `run_jobs` / `wait_for_counter`. No allocations in hot path (fixed pools, ring buffers).

### Rendering

- One-way data flow. Visit each renderable once per frame.
- GPU submission via sort keys. Decouple GPU scheduling from CPU scheduling.
- Visibility bitmasks — no concurrent mutation of same object.

### ECS

- Plugin-defined render hooks. Render graph modules.
- ECS itself is a plugin (replaceable scene model).

**Source**: Machinery blog archive (ruby0x1.github.io/machinery_blog_archive)

---

## Bevy (Rust ECS)

- Systems declare access via `SystemParam`. Scheduler builds dependency graph.
- Non-conflicting systems run in parallel (borrow checker semantics).
- `ParamSet` for disjoint mutability within one system.
- Parallel queries split across tables/archetypes into batches.
- **Main world + render world** separation. `Extract` systems copy to render-side state.

**Source**: Bevy docs, PR #14252 "Persistent Render World"

---

## EnTT (C++ ECS)

- Sparse sets: Entity IDs → dense packed arrays for cache-friendly iteration.
- Groups for SoA-style layouts.
- `par_each` for parallel iteration (huge wins when work per entity is large, overhead when tiny).
- Parallel paths must NOT structurally change registry during iteration.

**Source**: EnTT docs (skypjack.github.io/entt)

---

## Double/Triple Buffering

### Sim/Render Double Buffer

- Sim writes buffer A, render reads buffer B, swap at fence.
- One writer, one reader per buffer → no contention.
- Variable-size state handled via stable entity IDs, free lists, deferred structural changes.

### Triple Buffering

- Usually refers to GPU swap chain / present queue, not three sim copies.
- Reduces latency between frame production and display.

---

## Frame/Render Graphs (Frostbite, Vulkan engines)

- DAG of render passes and resources. Transient resources with automatic barriers.
- Memory aliasing: Resources that don't overlap in time share physical memory.
- Same dependency scheduling as job DAGs but also solves lifetime, barriers, queue semantics.

**Source**: O'Donnell, *FrameGraph: Extensible Rendering Architecture in Frostbite*, GDC 2017

---

## Data-Oriented Design (Mike Acton, CppCon 2014)

- Organize data for the hardware: SoA (struct of arrays), contiguous runs, fewer indirections.
- Enables job systems: Split arrays by index ranges → each job owns disjoint slice.
- No false sharing when jobs touch different cache lines.
- Prefetcher-friendly sequential access.

**Source**: Acton, CppCon 2014 (youtube.com/watch?v=rX0ItVEVjHc)

---

## Deterministic Simulation

### Factorio

- Single-threaded sim core. Parallelism only where order doesn't matter.
- Determinism by construction. Performance from heavy DOD-style inner loops.
- Dev statement: "Almost everything depends on everything else."

**Source**: Factorio forums (forums.factorio.com/viewtopic.php?t=49281)

### Age of Empires IV ("The MAW")

- Safely multithreading deterministic gameplay with tooling/validation.
- Lockstep networking requires bit-exact simulation across all clients.

**Source**: World's Edge, GDC Vault

### General

- Floating-point non-determinism is a real threat. Fixed-point or same-platform constraints.
- Parallel sim possible with **proven non-overlap** (machine-checked or by construction).

---

## World Snapshots / Versioning

- Explicit copying or versioning at tick boundaries.
- Cost of copy vs structural sharing tradeoff.
- ECS chunk-level copy-on-write: Memory scales with changes, not total world size.
- Unity Ghost Snapshots for networking: Per-tick snapshots with prediction/interpolation.

---

## Key Takeaways for Fabrica

1. **Schedule with dependency graphs** (CountdownEvent or atomic counters) — universal pattern.
2. **Partition data per-job** — disjoint array slices, no shared mutable state during execution.
3. **One-way pipeline** for sim → render — already have ProducerConsumerQueue for this.
4. **Deferred structural changes** — command buffers flushed at phase boundaries.
5. **Frame-scoped memory** — per-thread arenas or pools that reset each tick.
6. **Validate with tooling** — Bungie's resource tracking approach is worth considering.
