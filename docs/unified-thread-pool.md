# Unified Thread Pool — Design Investigation

## Problem

Both `SimulationCoordinator` and `RenderCoordinator` create independent `WorkerGroup` instances. Each group pins its
workers starting at core 0, so simulation workers 0..N and render workers 0..N compete for the same cores. More
fundamentally, when simulation finishes its tick work quickly but rendering is behind (or vice versa), the idle
workers in one group cannot help the other — cores sit unused.

## Industry Precedent

The game engine industry has strongly moved toward unified job systems with a single worker pool:

- **Naughty Dog** (GDC 2015): 6 worker threads locked to cores, executing both simulation and rendering jobs from
  the same pool via fibers. All engine work goes through one system.
- **Destiny/Bungie** (GDC 2015): Converted from "System-on-a-Thread" (Halo: Reach) to a unified job graph where the
  same worker pool handles all subsystems. Major CPU utilization improvement.
- **Unity DOTS**: Single job system with the Burst compiler — simulation, rendering prep, physics, AI all submit jobs
  to the same pool.
- **Intel Games Task Scheduler (GTS)**: Work-stealing microscheduler designed for automatic load balancing across
  subsystems sharing workers.

The consensus: dedicated thread pools per subsystem waste cores.

## Fabrica Context

Simulation and rendering are already temporally pipelined — they don't touch the same data simultaneously:

- **Simulation tick**: reads immutable `PreviousImage`, writes into a fresh `NextImage`. All workers share the same
  input/output.
- **Render frame**: reads immutable `Previous` and `Latest` chain nodes. All workers share the same input.

Neither subsystem's workers need to touch each other's data. A single thread can run a simulation job on one dispatch
and a render job on the next. There is no thread-local state that permanently binds a thread to one role.

## Design Options

### Option A: Full Job Queue

Replace the fixed dispatch-all-workers model with a job queue. Each "dispatch" submits N jobs (sim or render) to a
shared queue, and any available worker picks them up.

**Pros:** Maximum flexibility, natural work stealing, proven at scale (Naughty Dog, Destiny, Unity).

**Cons:** Significant refactor — replaces the synchronous `Dispatch → WaitAll` model with async job
submission/completion. Requires a job scheduler, dependency tracking, and a fundamentally different coordination
pattern.

### Option B: Dynamic Partitioning

Keep the current `Dispatch → WaitAll` pattern, but have a single pool of N workers that are dynamically partitioned.
The coordinator decides each frame: "workers 0-5 do simulation, workers 6-7 do rendering" (or any split). The ratio
adapts based on measured timing.

**Pros:** Much simpler than a full job system. Preserves the synchronous dispatch/join model. Adaptation logic is
localized.

**Cons:** Workers in one partition are idle during the other partition's work. The improvement over two separate pools
is that the split ratio adapts, but workers don't interleave sim and render work within a single frame.

### Option C: Unified Pool with Heterogeneous Executors

Each worker thread holds both executor types and a role flag. On each dispatch, the coordinator assigns each worker a
role (sim or render) along with the appropriate state. Between dispatches, any worker can switch roles.

**Pros:** Keeps the park/signal model, gets the adaptive split, threads stay pinned to cores.

**Cons:** The `IThreadExecutor<TState>` interface is generic on `TState`, so a single worker can't easily hold two
different executors. Options: (a) use a discriminated union / `object`-based dispatch (losing JIT specialization), or
(b) have workers hold both executor types and branch on a role flag.

## Key Tension: JIT Specialization vs. Flexibility

The current architecture's greatest strength — struct-generic JIT specialization via `IThreadExecutor<TState>` — is
also what makes a fully unified pool harder. `WorkerGroup<SimulationTickState, SimulationExecutor>` compiles to
specialized, inlined code for that exact type pair. A unified pool that can run either sim or render work would need
to dispatch through either a virtual call or a branch.

However, the actual work inside each executor (belt/machine computation, rendering) will dwarf the dispatch overhead.
The zero-overhead dispatch is optimizing for a cost that will be negligible relative to the real work.

## Recommendation

**Option B (dynamic partitioning)** is the pragmatic middle ground:

1. One set of N threads, one per core, all pinned — no core overlap.
2. A top-level "engine scheduler" owns the pool and decides the split each frame.
3. Simulation dispatches to its allocated subset; rendering dispatches to its allocated subset.
4. The split adapts based on measured timing (sim tick duration vs render frame duration).
5. Minimum 1 thread per side, so neither is ever starved.

This gets the core benefit (no wasted cores, adaptive allocation) without the complexity of a full job system. It is
also a stepping stone — if we later want Option A (full job queue), the unified pool is already in place.

### Concrete Changes for Option B

- `WorkerGroup` evolves to accept a `coreIndexOffset` or the pool is externalized.
- A new `EngineThreadPool` owns all N workers and exposes `Span<ThreadWorker>` slices.
- `SimulationCoordinator` and `RenderCoordinator` receive their worker slice from the pool rather than constructing
  their own.
- An `AdaptiveScheduler` measures sim tick time and render frame time, then adjusts the split for the next frame.
- Each worker holds both a `SimulationExecutor` and a `RenderExecutor` (both are cheap value-type structs), and the
  dispatcher sets the active role before signaling.

## Sources

- [Parallelizing the Naughty Dog Engine Using Fibers (GDC 2015)](https://gdcvault.com/play/1022186/Parallelizing-the-Naughty-Dog-Engine-Using-Fibers)
- [Multithreading the Entire Destiny Engine (GDC 2015)](https://www.gdcvault.com/play/1022164/Multithreading-the-Entire-Destiny)
- [Multithreaded Job Systems in Game Engines (PulseGeek)](https://pulsegeek.com/articles/multithreaded-job-systems-in-game-engines/)
- [Intel Games Task Scheduler](https://www.intel.com/content/www/us/en/developer/articles/technical/games-task-scheduler.html)
- [Destiny Multithreading Study (Ecy)](https://xrhoys.substack.com/p/a-study-on-destinys-multithreading)
