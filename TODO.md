# TODO

Tracked work items for Fabrica. Roughly prioritized within each section.

## World State (the actual game)

- [ ] Belt state — item transport using the unit/speed system already defined in `SimulationConstants`
- [ ] Machine state — producers, consumers, inserters
- [ ] Persistent tree structure for `WorldImage` — share unchanged subtrees across ticks so memory scales with changes-per-tick, not total world size (mentioned in `WorldImage` doc comment)
- [ ] Actual world advance logic in `SimulationLoop.Tick` (currently a TODO)

## Engine / Architecture — Features

- [ ] **Consider `Task`/`TaskCompletionSource` for loop thread management** — `Host.Run` currently tracks exceptions manually with `Exception?` locals and re-throws after `Thread.Join`. Wrapping each thread in a `TaskCompletionSource` would let us use `Task.WhenAll`, automatic exception propagation via `AggregateException`, and standard `async` composition. We should still use dedicated `Thread` objects for the actual loop execution (control over naming, background flag, stack size), but layer the `Task` abstraction on top for lifecycle/error tracking
- [ ] Populate `EngineStatistics` with live data — tick rate, pool pressure, frame times, producer/consumer throughput (struct exists as placeholder)
- [ ] Multi-threaded simulation — wire real per-worker tick computation into `SimulationExecutor.Execute()` (generic `ThreadWorker`/`WorkerGroup` infrastructure and dispatch cycle are in place)
- [ ] Multi-threaded rendering — wire real per-worker render computation into `RenderExecutor.Execute()` (`RenderCoordinator`/`WorkerGroup` infrastructure is in place; consumption loop dispatches through it when provided)
- [ ] Thread pinning on macOS — current `ThreadPinning` supports Windows/Linux; macOS needs `thread_policy_set` with `THREAD_AFFINITY_POLICY` for hint-based co-location
- [ ] Thread pinning for >64 cores (low priority) — workers beyond index 63 simply run unpinned; would need `SetThreadGroupAffinity` on Windows, larger `cpu_set_t` on Linux

## Testing

- [ ] Tests for production adapters (`SystemClock`, `ThreadWaiter`, `TaskSaveRunner`) if they gain non-trivial logic
- [ ] `WorldImage` standalone tests as it gains real state

## Quality / Tooling

- [ ] Consider `Directory.Build.props` for shared project settings as more projects are added

## Engine / Architecture — Observability & Diagnostics

- [ ] **Deferred consumer error reporting** — `DeferredConsumerScheduler.DrainCompletedTasks` silently reschedules faulted tasks (a `Debug.WriteLine` was added as a stopgap). Two things are needed: (1) real structured logging (e.g. Serilog) so errors are persisted to disk, not just debug output; (2) an end-to-end internal path that surfaces deferred consumer failures in the UI/user flow (e.g. via `EngineStatistics` or a similar observable channel) so the player or developer can see that a background operation failed, not just the log file
- [ ] **Instrument epoch gap, throttle depth, pool high-water** — Kafka/Flink-style lag metrics catch production issues early. Wire into `EngineStatistics`
- [ ] **Profile LOH and Gen2 collections** — as `WorldImage` grows with real state, pooled objects may cross the 85KB LOH threshold. Profile allocation patterns and GC pause impact
- [ ] **Benchmark wake primitives** — `ManualResetEventSlim` (spin-then-block) vs `AutoResetEvent` (kernel-only) under the actual dispatch pattern; measure wake latency on both x64 and ARM64
- [ ] **Benchmark struct-constrained interfaces** on ARM64 and x64 — validate JIT specialization and inlining assumptions with BenchmarkDotNet

## Engine / Architecture — Determinism & Safety

- [ ] **Formalize the memory model contract** — document the happens-before relationship between writer-thread field writes and the volatile chain publish. Consider `Volatile.Read/Write` or `Interlocked.Exchange` for auditability over raw `volatile` fields
- [ ] **Determinism checklist** — fixed `dt`, ordered inputs, no float nondeterminism unless audited. Parallel workers inside a sim tick must join in deterministic order for reproducibility
- [ ] **Core pinning overlap** — both sim and render worker groups pin starting from core 0; potential contention if running simultaneously. Consider offset allocation

## Documentation

- [ ] Architecture diagram (the mermaid-style flow in `Engine.cs` comments could become a standalone doc)
- [ ] Onboarding notes for the threading model — the doc comments are thorough but scattered across files
- [ ] **Document pinning rules** — analogous to hazard pointers / RCU grace periods; explain when Pin/Unpin must be called relative to epoch advancement

---

# Completed

## Engine / Architecture

- [x] ThreadWorker deadlock — wrapped execute path in `try/finally { _doneSignal.Set(); }`; workers now cancellation-aware via `WaitHandle.WaitAny` (PRs #45, #54)
- [x] `frameStart` sampling order — clamped `frameStart = Math.Max(frameStart, latestNode.PublishTimeNanoseconds)` to prevent negative elapsed (PR #51)
- [x] Cancellation responsiveness in tick loop — added `ThrowIfCancellationRequested()` between ticks in `ProcessAvailableTicks` (PR #49)
- [x] Top-level exception handling on loop threads — `Host.Run` wraps threads in try/catch with linked CTS cross-cancellation and exception propagation (PR #50)
- [x] Unhandled OCE crash — replaced `when` filter with two-catch pattern; hardened `ConsumptionLoop.RunOneIteration` with `ThrowIfCancellationRequested` (PR #56)
- [x] Worker shutdown — replaced explicit `Shutdown()` plumbing with cancellation-aware workers that self-terminate via `WaitHandle.WaitAny` on the cancellation token (PR #54)
- [x] Fix IRenderer docs — removed false claim about null Previous on first frame (PR #47)
- [x] Fix Host.cs pool exhaustion docs — rewrote to describe epoch-gap backpressure (PR #48)
- [x] Fix RenderFrame.cs Chain doc — removed null Previous inconsistency (PR #53)
- [x] Fix WorldImage.cs comment — `LatestSnapshot` → `LatestNode` (PR #53)
- [x] Deferred consumer error logging — added `Debug.WriteLine` stopgap (PR #52); real solution tracked separately above
- [x] Eliminate abbreviated variable names — codebase-wide rename of `cts`, `tcs`, `mut`, `seq`, `prev`, etc. (PR #55)
- [x] Observe `Task.Run` results in `TaskSaveRunner` — save exceptions are now captured via `ConcurrentQueue<SaveEvent>` and surfaced through `EngineStatus` in the `RenderFrame` ([ea8ae2d](https://github.com/CyrusNajmabadi/Fabrica/commit/ea8ae2d))
- [x] One-tick-behind interpolation — consumption loop holds two snapshots, computes interpolation timing, renderer blends between real simulation states ([d1c4c9a](https://github.com/CyrusNajmabadi/Fabrica/commit/d1c4c9a))
- [x] Snapshot lifetime contract — renderers must not store snapshot refs beyond the Render call; documented in `IRenderer` and `RenderFrame` ([d1c4c9a](https://github.com/CyrusNajmabadi/Fabrica/commit/d1c4c9a))
- [x] Move `TickNumber` and `PublishTimeNanoseconds` from `WorldImage` to `WorldSnapshot` — publication metadata belongs on the snapshot, not the world state ([d1c4c9a](https://github.com/CyrusNajmabadi/Fabrica/commit/d1c4c9a))
- [x] Tighten type safety — mutable public fields on `WorldSnapshot` replaced with private-set properties; `WorldImage.Reset()` renamed to `ResetForPool()` ([d1c4c9a](https://github.com/CyrusNajmabadi/Fabrica/commit/d1c4c9a))
- [x] ObjectPool redesign — growable `Stack<T>`-backed pool; `Rent()` always returns (allocates on demand), `Return()` always accepts; pre-allocation preserved for cache warmth ([032a1dc](https://github.com/CyrusNajmabadi/Fabrica/commit/032a1dc))
- [x] Time-based backpressure — tick-epoch gap measured in nanoseconds with a 100ms low water mark (soft exponential delay) and a 2s hard ceiling (simulation blocks until consumption catches up); replaces pool-availability-based throttling ([032a1dc](https://github.com/CyrusNajmabadi/Fabrica/commit/032a1dc))
- [x] SimulationWorker stub — design placeholder documenting per-worker pools, created-nodes list for deferred ref-counting, and the threading contract for future multi-threaded simulation ([032a1dc](https://github.com/CyrusNajmabadi/Fabrica/commit/032a1dc))
- [x] Save overlap — analyzed and confirmed as a non-issue: `NextSaveAtTick` is set to 0 before dispatch and only rescheduled in the save task's `finally` block, so no second save can trigger while one is in flight
- [x] Generic `ThreadWorker<TState, TExecutor>` refactor — extracted thread park/signal loop, shutdown, and pinning into reusable generic infrastructure (`ThreadWorker`, `WorkerGroup`, `IThreadExecutor`); `SimulationWorker` deleted and replaced by `SimulationExecutor` + generic worker; `RenderCoordinator`/`RenderExecutor` built on the same infrastructure for parallel rendering; `Simulator` renamed to `SimulationCoordinator` for naming consistency

## Testing

- [x] True concurrency stress tests — 5 real multi-threaded tests (throughput, backpressure, shutdown, worker lifecycle, save pinning) with hard invariant checks ([e23657d](https://github.com/CyrusNajmabadi/Fabrica/commit/e23657d))
- [x] Backpressure adaptation tests — 7 deterministic multi-phase tests verifying the feedback loop adapts to matched rates, slow consumption, catch-up, re-engagement, exponential delays, hard ceiling, and bounded gap ([d697ce8](https://github.com/CyrusNajmabadi/Fabrica/commit/d697ce8))
- [x] xUnit v3 upgrade — migrated from xUnit 2.9.3 to xUnit v3 3.2.2 with aggressive parallel algorithm ([6057e02](https://github.com/CyrusNajmabadi/Fabrica/commit/6057e02))
- [x] Consolidate duplicate test infrastructure — shared `TestDoubles.cs` with `Test`-prefixed interface implementations, unified `WaiterState` families, 15 new MemorySystem/ObjectPool tests ([9a4b2bc](https://github.com/CyrusNajmabadi/Fabrica/commit/9a4b2bc))

## Quality / Tooling

- [x] `.editorconfig` for consistent formatting — rules for `var`, expression bodies, braces, `this.` qualification, naming; all elevated to warnings ([8b0d326](https://github.com/CyrusNajmabadi/Fabrica/commit/8b0d326), [cc97720](https://github.com/CyrusNajmabadi/Fabrica/commit/cc97720))
- [x] Coverage tooling — Coverlet collector added to test project; coverage uploaded to Codecov with badge on README ([7fe7d64](https://github.com/CyrusNajmabadi/Fabrica/commit/7fe7d64), [1ca02ed](https://github.com/CyrusNajmabadi/Fabrica/commit/1ca02ed))
- [x] CI pipeline — GitHub Actions workflow with separate build and test checks; coverage summary in run page ([7fe7d64](https://github.com/CyrusNajmabadi/Fabrica/commit/7fe7d64), [1ca02ed](https://github.com/CyrusNajmabadi/Fabrica/commit/1ca02ed))
