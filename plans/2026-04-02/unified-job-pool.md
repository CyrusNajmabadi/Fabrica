# Unified Work-Stealing Job Pool

## Overview

Replace the current two-separate-WorkerGroup model with a unified work-stealing job pool where every
core runs one pinned worker thread and both simulation and rendering submit jobs to the same pool.
Build the core data structures and scheduler first (with tests), then integrate with the engine in a
later phase.

---

## Current Architecture (What We Have)

- **2 coordinator threads** (Production + Consumption) started by `Host.RunAsync`
- **N simulation workers** (`WorkerGroup` in `SimulationCoordinator`)
- **M render workers** (`WorkerGroup` in `RenderCoordinator`)
- Total: 2 + N + M threads, often exceeding core count
- Sim workers can never help render; render workers can never help sim
- Coordinator threads are mostly idle (blocked on `WaitAll` or sleeping)

## Target Architecture

- **One thread per core, pinned** — all threads are workers in a shared pool
- **Threads 0 and 1 also run coordinator logic** (tick loop / frame loop)
- When coordinators wait for sub-jobs, they **steal and execute pool jobs** instead of blocking
- Both sim and render submit to the same pool — automatic load balancing via work stealing

This is **Option B** from the research: coordinators ARE pool workers. Zero wasted cores. Coordinator
ownership semantics (SPSC queue, ObjectPool thread-ID assertions, refcounting) are preserved because
each coordinator always runs on the same physical thread.

---

## Key Design Decisions

### 1. Coordinator Thread Ownership (Preserved)

The production coordinator always runs on Thread 0. The consumption coordinator always runs on
Thread 1. This preserves all single-writer invariants:

- `ProducerAppend` / `ProducerCleanup` — always Thread 0
- `ConsumerAcquire` / `ConsumerAdvance` — always Thread 1
- `ObjectPool.Rent/Return` — always Thread 0 (debug thread-ID assertion holds)
- `_pinnedPayloads` dictionary — always Thread 0
- `DeferredConsumerScheduler` — always Thread 1

The coordinator's loop naturally takes priority over stolen work: the loop runs first, and only
during the "waiting for sub-jobs" window does the thread steal from the pool.

### 2. Cooperative Jobs and Time Tracking (Future)

Rather than static size hints, jobs will eventually be cooperative with respect to time. The system
will track how long jobs take to execute. Jobs that consistently run long can either:

- Be broken into smaller sub-jobs by the submitter
- Yield mid-execution by re-enqueuing themselves at the end of the queue

This gives us both coordinator responsiveness (a coordinator thread won't be stuck in a long stolen
job) and telemetry (we can identify which jobs need splitting). For the initial implementation, we
target small job granularity (< 0.5ms) by convention and add the cooperative machinery later.

### 3. Zero-Allocation Job Execution (Deferred)

The current codebase uses `IThreadExecutor<TState>` with struct constraints to eliminate virtual
dispatch and heap allocation. For the unified pool, we eventually need heterogeneous job types in a
shared deque without per-job allocation.

For now, we use whatever representation is simplest to get the architecture right (e.g., `Action`
delegates or a simple `IJob` interface). Zero-allocation optimization (function-pointer trampolines,
pooled wrappers, etc.) is a separate concern we'll address once the job system is proven correct and
we can profile real workloads.

### 4. Global Injection Queue (Staged)

When a coordinator pushes N jobs to its local deque, only that thread sees them locally — others must
steal. A global MPSC (multi-producer, single-consumer-per-steal) injection queue allows fast fan-out.
Workers check: local deque first, then global queue, then steal from peers.

This is an optimization we can add in a later PR after the core system works.

---

## Core Components (Build First, Integrate Later)

### Component 1: WorkStealingDeque<T>

The Chase-Lev lock-free deque. Each worker thread owns one.

- **Bottom (owner):** Push and Pop — LIFO, no synchronization (single writer)
- **Top (thieves):** TrySteal — FIFO, atomic CAS on top index
- Backed by a growable circular array (power-of-2 sizing, like our slab approach)
- Contention only on the last item (thief vs. owner race, resolved by CAS)

Location: `Fabrica.Core/Collections/WorkStealingDeque.cs`

### Component 2: JobCounter

An atomic counter for fork-join synchronization. Replaces `AutoResetEvent` + `WaitHandleBatch`.

- Initialized to N (number of jobs in a batch)
- Each job decrements on completion (`Interlocked.Decrement`)
- Submitter waits for zero (busy-loop executing stolen work between checks)
- No OS synchronization primitives in the hot path

Location: `Fabrica.Core/Threading/JobCounter.cs`

### Component 3: Job Representation

For the initial implementation, a simple representation — e.g., `Action` delegate or an `IJob`
interface. The deque stores these directly. Zero-allocation optimization comes later once the
architecture is proven.

Location: `Fabrica.Core/Threading/` (exact shape TBD during implementation)

### Component 4: JobPool

The central scheduler. Owns N worker threads, each pinned to a core with a local deque.

**Worker loop:**
1. Pop from own deque (LIFO)
2. If empty, steal from random peer's deque (FIFO)
3. If found, execute and goto 1
4. Spin briefly (SpinWait)
5. If still nothing, park on event (power-efficient sleep)
6. When woken, goto 1

**Submit:**
1. Push jobs to the submitting thread's local deque
2. Wake sleeping workers if any

**WaitForJobs (called by coordinators):**
1. While counter > 0:
   a. Try pop from own deque — execute if found
   b. Try steal from peer (prefer Small jobs if this is a coordinator thread)
   c. If nothing, SpinWait briefly
2. Counter reached 0 — return to coordinator work

Location: `Fabrica.Core/Threading/JobPool.cs`

---

## What Stays Unchanged

- `ProducerConsumerQueue<T>` — SPSC queue between production and consumption
- `SharedPipelineState<T>` — groups queue and pinned versions
- `PipelineEntry<T>` — payload wrapper with tick and timestamp
- `PinnedVersions` — ConcurrentDictionary pin registry
- `ObjectPool<T>` — single-threaded pool (stays on Thread 0)
- `ProductionLoop` / `ConsumptionLoop` — coordinator logic (tick loop / frame loop)
- `Host.RunAsync` — starts coordinator threads (now they also participate in pool)

## What Changes (Later, After Core Is Built)

- `WorkerGroup` — replaced by `JobPool`
- `SimulationCoordinator.AdvanceTick` — submits sim jobs to shared pool instead of `Dispatch`
- `RenderCoordinator.DispatchFrame` — submits render jobs to shared pool instead of `Dispatch`
- `IThreadExecutor<TState>` — evolves into `IJob` or similar
- Thread pinning — moves from individual WorkerGroups into the centralized JobPool
- `Host` — creates the JobPool and passes it to both loops

---

## Implementation Phases (One PR Each)

### PR 1: WorkStealingDeque<T>

Build the Chase-Lev deque as a standalone data structure in `Fabrica.Core.Collections`.
Single-threaded correctness tests + multi-threaded stress tests (owner push/pop concurrent with
thieves stealing). This is well-studied with known correctness proofs.

### PR 2: JobCounter

Build the atomic counter for fork-join synchronization. Simple type, straightforward tests.

### PR 3: Job Representation + Submission API

Define the job type (simple for now — optimize later) and the submission API for pushing jobs into
the pool. This PR is where we nail down the generic job API shape.

### PR 4: JobPool (Workers + Stealing)

Build the pool with N pinned worker threads, each running the pop/steal/spin/park loop. Test with
synthetic workloads: submit batches, verify all complete, measure throughput and latency.

### PR 5: WaitForJobs (Coordinator Participation)

Add the "wait while stealing" mechanism so coordinator threads can participate. Add size-hint
filtering so coordinators prefer small stolen jobs. Test that coordinators resume promptly.

### PR 6: Integration — Replace WorkerGroup

Swap `SimulationCoordinator` and `RenderCoordinator` to use the shared `JobPool`. Update `Host` to
create the pool. Verify all existing tests pass. Stress tests with the real pipeline.

### PR 7+ (Future)

- Zero-allocation job representation (function-pointer trampolines, pooled wrappers, etc.)
- Cooperative job time tracking — measure execution time, identify long jobs, support yield/re-enqueue
- Priority levels for jobs
- Global injection queue for faster fan-out
- Quality scaling feedback loop for rendering
