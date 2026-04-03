# Research: Unified Job Systems — Why the Industry Abandoned Dedicated Thread Pools

*Conducted 2026-04-02. Deep research across AAA engine talks, control theory, and real-world
implementations for dynamic resource allocation between simulation and rendering.*

---

## The Core Finding

**Every major modern engine has converged on the same model: a unified job system with one worker
thread per CPU core.** No dedicated threads for simulation or rendering. All work is broken into
small jobs (0.05–0.5ms each) with dependency graphs, and a shared scheduler dispatches them.

The old "System-on-a-Thread" model (Halo: Reach) where each subsystem has its own dedicated thread
pool **does not scale** with modern core counts and wastes resources when one side is idle.

---

## Engine Case Studies

### Naughty Dog (The Last of Us / Uncharted)

- 6 worker threads pinned to cores
- 160 fibers, 3 priority queues
- Jobs can yield mid-execution (fiber switching) to avoid blocking a core
- All engine work goes through one system

**Source**: Gyrling, *Parallelizing the Naughty Dog Engine Using Fibers*, GDC 2015

### id Tech 7 (DOOM Eternal)

- Literally **no main thread and no render thread**
- ~100 jobs per frame, all dispatched across all cores
- Eliminated the dedicated scheduling thread that id Tech 6 had
- Most extreme example of the unified model

**Source**: Axel Gneiting interviews, technical analyses

### Destiny / Bungie

- Switched from Halo: Reach's System-on-a-Thread to a full job graph model
- Task scheduler with dependency tracking
- Huge CPU utilization improvement over the dedicated pool model

**Source**: Genova, *Multithreading the Entire Destiny Engine*, GDC 2015

### Unity DOTS

- Worker threads = core count, work-stealing scheduler
- All simulation and rendering jobs in one pool
- Burst compiler eliminates managed overhead for scheduled jobs

### Frostbite (EA/DICE)

- Job-based with FrameGraph for rendering
- Same unified pool pattern

---

## How the Unified Model Handles Overload

### Simulation Can't Hit Target

- Idle workers automatically pick up sim jobs when rendering isn't running
- If still too slow: **time dilation** — ticks happen less often in wall-clock time
- Determinism preserved (same operations per tick, just slower)

### Rendering Can't Hit Target

The industry answer is NOT "throw more threads" — it's **"reduce the work"**:

- **Dynamic Resolution Scaling (DRS)**: Unreal, Unity, NVIDIA DLSS. Feedback controller monitors
  frame time, adjusts render resolution between min/max.
- **LOD stepping**: Lower-detail meshes, fewer particles, disable post-processing.
- **Frame budget time-boxing**: Each phase gets a budget. Over budget → cull more aggressively.

Key insight: **Adding more cores to rendering has severe diminishing returns.** Rendering has serial
bottlenecks (draw call submission, GPU pipeline). Reducing work is more effective.

### Both Sides Behind

Prioritized graceful degradation:

1. **Rendering gets latency priority** — consistent 30fps > alternating 20/50fps
2. **Simulation slows gracefully** — time dilation at 0.8x speed, but frames are smooth
3. **Quality knobs turned down** on both sides

---

## Equilibrium Algorithms

### PID Controllers (Control Theory)

- Monitor metric (frame time, queue depth), compare to target, adjust control variable
- P (proportional), I (integral), D (derivative) terms
- **Practical experience is mixed**: .NET ThreadPool used hill-climbing (PID-related), **disabled
  by default in .NET 8** — unstable with mixed workloads, slow to respond, visible overhead

### Threshold-Based Heuristics (What Actually Ships)

- Measure frame time each frame
- Over budget for N consecutive frames → reduce quality one step
- Under budget for M consecutive frames → increase quality one step
- Hysteresis (different thresholds for up vs down) to prevent oscillation
- Used by: Unreal DRS, NVIDIA DLSS, most shipped engines

### Frame Graphs with Budget Allocation (Cutting Edge)

- Frostbite FrameGraph, Unreal RDG, Activision Task Graph Renderer
- Express entire frame as dependency graph of jobs
- Scheduler has whole-frame knowledge: topological sort, job pruning, memory aliasing,
  async-compute overlap
- Eliminates "how many cores per side" question — scheduler decides

---

## Spin-Then-Park Strategy for Idle Workers

Real work-stealing schedulers use escalating idle strategies:

1. **Spin** — Brief user-mode spinning (few microseconds). Catches quick bursts of work.
2. **Yield** — `Thread.Yield()` or similar. Give up timeslice but remain schedulable.
3. **Park** — Block on OS primitive (event/futex). Zero CPU usage. Wake on signal.

.NET's `CountdownEvent` is suitable for fork-join synchronization:
- `IsSet` is a volatile read (no kernel involvement)
- `Signal()` is `Interlocked.Decrement` + optionally `ManualResetEventSlim.Set()`
- If you only poll `IsSet` and call `Signal/AddCount`, no kernel handle is ever allocated

---

## Implications for Fabrica

### Near Term (Current Architecture)

Two dedicated worker pools with SPSC queue is the Halo: Reach model. Works well when you have
few cores and the workload split is roughly known.

### Scaling Path (Unified Job System)

- One worker thread pinned per core (already have pinning infrastructure)
- Break simulation and rendering into small jobs with dependency edges
- Priority queues (High/Normal/Low) — rendering frame-critical jobs get High priority
- Work stealing for automatic load balancing
- SPSC queue still works as handoff between sim "phase" and render "phase"
- Coordinators participate as workers when waiting for sub-jobs

### For Handling Overload

Primary lever is **quality scaling, not thread reallocation**:
- Rendering: dynamic resolution, LOD, skip post-processing
- Simulation: time dilation (ticks slower, determinism preserved)
- Both: threshold-with-hysteresis feedback controller

**Key insight**: Don't converge on a thread allocation — converge on a quality level that fits
within your resource budget.

---

## The Architecture Decision: Coordinators as Workers

After discussion, Fabrica chose **Option B**: coordinators ARE pool workers.

- Thread 0 = sim coordinator + worker
- Thread 1 = render coordinator + worker
- All other threads = pure workers
- When coordinators wait for sub-jobs, they steal and execute work
- Zero wasted cores
- Coordinator ownership semantics preserved (always same physical thread)
- ObjectPool thread-ID assertions still hold

This matches Naughty Dog's model most closely.
