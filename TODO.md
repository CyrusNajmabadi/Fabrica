# TODO

Tracked work items for Fabrica. Roughly prioritized within each section.

## World State (the actual game)

- [ ] Belt state — item transport using the unit/speed system already defined in `SimulationConstants`
- [ ] Machine state — producers, consumers, inserters
- [ ] Persistent tree structure for `WorldImage` — share unchanged subtrees across ticks so memory scales with
  changes-per-tick, not total world size (mentioned in `WorldImage` doc comment)
- [ ] Actual world advance logic in `SimulationExecutor.Execute()` (currently a no-op)

## Engine / Architecture — Features

- [ ] Populate `EngineStatistics` with live data — tick rate, pool pressure, frame times, producer/consumer throughput
  (struct exists as placeholder)
- [ ] Multi-threaded simulation — wire real per-worker tick computation into `SimulationExecutor.Execute()` (generic
  `ThreadWorker`/`WorkerGroup` infrastructure and dispatch cycle are in place)
- [ ] Multi-threaded rendering — wire real per-worker render computation into `RenderExecutor.Execute()`
  (`RenderCoordinator`/`WorkerGroup` infrastructure is in place; consumption loop dispatches through it when provided)
- [ ] Thread pinning on macOS — current `ThreadPinning` supports Windows/Linux; macOS needs `thread_policy_set` with
  `THREAD_AFFINITY_POLICY` for hint-based co-location
- [ ] Thread pinning for >64 cores (low priority) — workers beyond index 63 simply run unpinned; would need
  `SetThreadGroupAffinity` on Windows, larger `cpu_set_t` on Linux

## Testing

- [ ] Tests for production adapters (`SystemClock`, `ThreadWaiter`, `TaskSaveRunner`) if they gain non-trivial logic
- [ ] `WorldImage` standalone tests as it gains real state

## Engine / Architecture — Observability & Diagnostics

- [ ] **Deferred consumer error reporting** — `DeferredConsumerScheduler.DrainCompletedTasks` silently reschedules
  faulted tasks (a `Debug.WriteLine` was added as a stopgap). Two things are needed: (1) real structured logging (e.g.
  Serilog) so errors are persisted to disk, not just debug output; (2) an end-to-end internal path that surfaces deferred
  consumer failures in the UI/user flow (e.g. via `EngineStatistics` or a similar observable channel) so the player or
  developer can see that a background operation failed, not just the log file
- [ ] **Instrument epoch gap, throttle depth, pool high-water** — Kafka/Flink-style lag metrics catch production issues
  early. Wire into `EngineStatistics`
- [ ] **Profile LOH and Gen2 collections** — as `WorldImage` grows with real state, pooled objects may cross the 85KB LOH
  threshold. Profile allocation patterns and GC pause impact
- [ ] **Benchmark wake primitives** — `ManualResetEventSlim` (spin-then-block) vs `AutoResetEvent` (kernel-only) under the
  actual dispatch pattern; measure wake latency on both x64 and ARM64
- [ ] **Benchmark struct-constrained interfaces** on ARM64 and x64 — validate JIT specialization and inlining
  assumptions with BenchmarkDotNet

## Engine / Architecture — Coordinator Merge

- [ ] **Root tracking for SnapshotSlice integration** — the merge pipeline currently establishes refcounts for internal
  children but does not identify which newly-merged nodes are snapshot roots. The likely approach: jobs explicitly mark
  root handles at creation time via a per-thread `RootCollector` buffer (alongside the TLB). After merge, the
  coordinator remaps collected root handles from local to global and feeds them into `SnapshotSlice.AddRoot`.
  - **Why not refcount-zero:** a zero refcount could indicate a bug (leaked node), not a root. The correct invariant
    is: debug-assert that all declared roots have refcount zero after Phase 2b, and no non-roots have refcount zero.
  - **Why not root-job-output:** pushes root-marking responsibility far from the creation point, making it hard to
    reason about. Jobs know at creation time what they're building; marking roots there keeps the decision local.

## Engine / Architecture — Coordinator Merge Optimizations

- [ ] **Fine-grained merge overlap with production jobs** — the baseline coordinator merge waits for the entire production
  DAG to finish before starting any merge work. A more aggressive approach uses two layers of tracking to start merge
  work for each type as early as possible:
  - **Static layer (source generator):** the type reachability graph prunes impossible combinations at compile time. A
    job's "type footprint" (the set of node types it or its sub-jobs might create) can never include types that are
    statically unreachable from its declared inputs. This is zero runtime cost.
  - **Dynamic layer (per-type outstanding counters):** each job declares its type footprint. When enqueued, per-type
    counters are incremented for every type in the footprint. When a job completes (or explicitly releases a type it
    chose not to use — e.g., it decided not to spawn a sub-job that would have created that type), those counters are
    decremented. When a type's counter hits zero, merge Phase 1 for that type becomes eligible.
  - This is the same atomic-counter dependency pattern used for job DAG scheduling, applied to type-production tracking.
    The dependency chain becomes: Phase1(T) depends on T's production counter hitting zero; Phase2a(P) depends on
    Phase1(P) + Phase1(C) for every child type C of P; Phase2b(T) depends on Phase2a(P) for every parent type P with
    `Handle<T>` children.
  - Net effect: merge work overlaps with late-running production jobs, reducing total tick latency. Early-finishing types
    begin merging while unrelated types are still being produced.

## Engine / Architecture — Determinism & Safety

- [ ] **Unified thread pool with dynamic sim/render allocation** — both sim and render worker groups currently create
  independent pools pinned from core 0, wasting cores when one side is idle. Replace with a single pool of N threads
  (one per core) that dynamically partitions between simulation and rendering based on measured load. Full design
  investigation and options analysis in [`docs/unified-thread-pool.md`](docs/unified-thread-pool.md)
- [ ] **Renderer sharing the `JobScheduler` work queue** — the new `JobScheduler` is designed for simulation work.
  Discuss how the future renderer will share or coordinate with this work queue: separate scheduler instance, shared
  workers with priority lanes, or time-sliced access between sim and render phases

## Documentation

- [ ] Architecture diagram (the mermaid-style flow in `Engine.cs` comments could become a standalone doc)
- [ ] Onboarding notes for the threading model — the doc comments are thorough but scattered across files

---

# Completed

## Engine / Architecture

- [x] Formalize the memory model contract — replaced `volatile` fields with `Volatile.Read/Write` in
  `SharedPipelineState`; documented cross-field independence invariant (PR #58)
- [x] Determinism checklist — documented constraints on `SimulationConstants`, `SimulationExecutor` (independent
  partitions vs deterministic merge), and `IProducer.Produce` (canonical input ordering) (PR #59)
- [x] ThreadWorker deadlock — wrapped execute path in `try/finally { _doneSignal.Set(); }`; workers now
  cancellation-aware via `WaitHandle.WaitAny` (PRs #45, #54)
- [x] `frameStart` sampling order — clamped `frameStart = Math.Max(frameStart, latestNode.PublishTimeNanoseconds)` to
  prevent negative elapsed (PR #51)
- [x] Cancellation responsiveness in tick loop — added `ThrowIfCancellationRequested()` between ticks in
  `ProcessAvailableTicks` (PR #49)
- [x] Top-level exception handling on loop threads — `Host.Run` wraps threads in try/catch with linked CTS
  cross-cancellation and exception propagation (PR #50)
- [x] Unhandled OCE crash — replaced `when` filter with two-catch pattern; hardened `ConsumptionLoop.RunOneIteration`
  with `ThrowIfCancellationRequested` (PR #56)
- [x] Worker shutdown — replaced explicit `Shutdown()` plumbing with cancellation-aware workers that self-terminate via
  `WaitHandle.WaitAny` on the cancellation token (PR #54)
- [x] Fix IRenderer docs — removed false claim about null Previous on first frame (PR #47)
- [x] Fix Host.cs pool exhaustion docs — rewrote to describe epoch-gap backpressure (PR #48)
- [x] Fix RenderFrame.cs Chain doc — removed null Previous inconsistency (PR #53)
- [x] Fix WorldImage.cs comment — `LatestSnapshot` → `LatestNode` (PR #53)
- [x] Deferred consumer error logging — added `Debug.WriteLine` stopgap (PR #52); real solution tracked separately above
- [x] Eliminate abbreviated variable names — codebase-wide rename of `cts`, `tcs`, `mut`, `seq`, `prev`, etc. (PR #55)
- [x] Observe `Task.Run` results in `TaskSaveRunner` — save exceptions are now captured via `ConcurrentQueue<SaveEvent>`
  and surfaced through `EngineStatus` in the `RenderFrame` ([ea8ae2d](https://github.com/CyrusNajmabadi/Fabrica/commit/ea8ae2d))
- [x] One-tick-behind interpolation — consumption loop holds two snapshots, computes interpolation timing, renderer blends
  between real simulation states ([d1c4c9a](https://github.com/CyrusNajmabadi/Fabrica/commit/d1c4c9a))
- [x] Snapshot lifetime contract — renderers must not store snapshot refs beyond the Render call; documented in `IRenderer`
  and `RenderFrame` ([d1c4c9a](https://github.com/CyrusNajmabadi/Fabrica/commit/d1c4c9a))
- [x] Move `TickNumber` and `PublishTimeNanoseconds` from `WorldImage` to `WorldSnapshot` — publication metadata belongs
  on the snapshot, not the world state ([d1c4c9a](https://github.com/CyrusNajmabadi/Fabrica/commit/d1c4c9a))
- [x] Tighten type safety — mutable public fields on `WorldSnapshot` replaced with private-set properties;
  `WorldImage.Reset()` renamed to `ResetForPool()` ([d1c4c9a](https://github.com/CyrusNajmabadi/Fabrica/commit/d1c4c9a))
- [x] ObjectPool redesign — growable `Stack<T>`-backed pool; `Rent()` always returns (allocates on demand), `Return()`
  always accepts; pre-allocation preserved for cache warmth ([032a1dc](https://github.com/CyrusNajmabadi/Fabrica/commit/032a1dc))
- [x] Time-based backpressure — tick-epoch gap measured in nanoseconds with a 100ms low water mark (soft exponential delay)
  and a 2s hard ceiling (simulation blocks until consumption catches up); replaces pool-availability-based throttling
  ([032a1dc](https://github.com/CyrusNajmabadi/Fabrica/commit/032a1dc))
- [x] SimulationWorker stub — design placeholder documenting per-worker pools, created-nodes list for deferred ref-counting,
  and the threading contract for future multi-threaded simulation ([032a1dc](https://github.com/CyrusNajmabadi/Fabrica/commit/032a1dc))
- [x] Save overlap — analyzed and confirmed as a non-issue: `NextSaveAtTick` is set to 0 before dispatch and only
  rescheduled in the save task's `finally` block, so no second save can trigger while one is in flight
- [x] Generic `ThreadWorker<TState, TExecutor>` refactor — extracted thread park/signal loop, shutdown, and pinning into
  reusable generic infrastructure (`ThreadWorker`, `WorkerGroup`, `IThreadExecutor`); `SimulationWorker` deleted and
  replaced by `SimulationExecutor` + generic worker; `RenderCoordinator`/`RenderExecutor` built on the same infrastructure
  for parallel rendering; `Simulator` renamed to `SimulationCoordinator` for naming consistency
- [x] `Task`/`TaskCompletionSource` for loop thread management — `Host.Run` replaced with `async Task RunAsync` using
  `TaskCompletionSource` per thread and `await Task.WhenAll`; automatic exception propagation via `AggregateException`;
  dedicated `Thread` objects retained for execution (PR #67)
- [x] Hot-path lambda allocation fix — replaced `CleanupStaleNodes` capturing lambda with manual `DrainUnpinnedNodes`
  using a pre-allocated drain buffer; replaced `IEnumerable` boxing in `ExceptWith` with explicit loop (PRs #65, #66)
- [x] Replace ChainNode linked list with `ProducerConsumerQueue` — lock-free SPSC slab-based queue replaces the
  `BaseProductionLoop` chain model; `ConsumerAdvance(count)` enables hold-back-one interpolation; `PinnedVersions`
  adapted to `long` queue positions; `BaseProductionLoop` and all chain node types deleted (PRs #83, #84)
- [x] Simplify `Segment` indexer — replaced three public indexers (`this[long]`, `this[int]`, `this[Index]`) with a
  single `this[Index]` backed by a private `ItemAt(long)` method; `int` implicitly converts to `Index` so no ambiguity
  (PR #84)
- [x] Add simulation tick to `PipelineEntry` — zero-based monotonically increasing tick stamped by `ProductionLoop`;
  debug invariant checking in `ProductionLoop` (tick/position lockstep), `ConsumptionLoop` (contiguous ticks, cross-frame
  continuity), and `StressTestMetrics` (runtime monotonicity) (PR #85)
- [x] Project structure refactor — split monolithic `Engine` project into `Fabrica.Pipeline` (generic pipeline/threading
  infrastructure), `Fabrica.Engine` (simulation/rendering), and `Fabrica.ConsoleApp` (entry point); introduced
  `PipelineConfiguration` to decouple pipeline from engine constants; no `InternalsVisibleTo` between production
  libraries (PR #68)

## Testing

- [x] True concurrency stress tests — 5 real multi-threaded tests (throughput, backpressure, shutdown, worker lifecycle,
  save pinning) with hard invariant checks ([e23657d](https://github.com/CyrusNajmabadi/Fabrica/commit/e23657d))
- [x] Backpressure adaptation tests — 7 deterministic multi-phase tests verifying the feedback loop adapts to matched
  rates, slow consumption, catch-up, re-engagement, exponential delays, hard ceiling, and bounded gap
  ([d697ce8](https://github.com/CyrusNajmabadi/Fabrica/commit/d697ce8))
- [x] xUnit v3 upgrade — migrated from xUnit 2.9.3 to xUnit v3 3.2.2 with aggressive parallel algorithm
  ([6057e02](https://github.com/CyrusNajmabadi/Fabrica/commit/6057e02))
- [x] Consolidate duplicate test infrastructure — shared `TestDoubles.cs` with `Test`-prefixed interface implementations,
  unified `WaiterState` families, 15 new MemorySystem/ObjectPool tests ([9a4b2bc](https://github.com/CyrusNajmabadi/Fabrica/commit/9a4b2bc))
- [x] Test project split — split `Fabrica.Tests` into `Fabrica.Pipeline.Tests` and `Fabrica.Engine.Tests` (PR #69);
  moved 7 pipeline test files out of Engine.Tests with `WorkerGroup`-backed test doubles (`TestWorkerProducer`,
  `TestWorkerConsumer`) so pipeline stress tests exercise the full threading infrastructure without engine
  dependencies (PR #71)
- [x] Migrate tests from ChainNode to `ProducerConsumerQueue` — rewrote all pipeline and engine test suites; deleted
  `ChainNodeTests` and `ChainNodePayloadTests`; stress test metrics now validate tick monotonicity (PR #83)

## Quality / Tooling

- [x] `.editorconfig` for consistent formatting — rules for `var`, expression bodies, braces, `this.` qualification,
  naming; all elevated to warnings ([8b0d326](https://github.com/CyrusNajmabadi/Fabrica/commit/8b0d326),
  [cc97720](https://github.com/CyrusNajmabadi/Fabrica/commit/cc97720))
- [x] Coverage tooling — Coverlet collector added to test project; coverage uploaded to Codecov with badge on README
  ([7fe7d64](https://github.com/CyrusNajmabadi/Fabrica/commit/7fe7d64), [1ca02ed](https://github.com/CyrusNajmabadi/Fabrica/commit/1ca02ed))
- [x] CI pipeline — GitHub Actions workflow with separate build and test checks; coverage summary in run page
  ([7fe7d64](https://github.com/CyrusNajmabadi/Fabrica/commit/7fe7d64), [1ca02ed](https://github.com/CyrusNajmabadi/Fabrica/commit/1ca02ed))
- [x] Comment wrapping at ~120 columns — reflowed all `.cs` and `.md` comments to ~120 column width with minimum 80
  char lines; codified in `.cursor/rules/comment-wrapping.mdc` (PRs #60, #61)
- [x] `Directory.Build.props` for shared project settings — centralized build properties and package versions (PR #81)
- [x] Branch protection on `master` — configured via GitHub API to require PR-based updates only

## Documentation

- [x] Document pinning protocol — single authoritative location in `PinnedVersions.cs` with cross-references from
  `IPinOwner`, `IDeferredConsumer`, `ProductionLoop`; performance characteristics documented (PRs #63, #64)
