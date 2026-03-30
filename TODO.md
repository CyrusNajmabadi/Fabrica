# TODO

Tracked work items for Fabrica. Roughly prioritized within each section.

## World State (the actual game)

- [ ] Belt state — item transport using the unit/speed system already defined in `SimulationConstants`
- [ ] Machine state — producers, consumers, inserters
- [ ] Persistent tree structure for `WorldImage` — share unchanged subtrees across ticks so memory scales with changes-per-tick, not total world size (mentioned in `WorldImage` doc comment)
- [ ] Actual world advance logic in `SimulationLoop.Tick` (currently a TODO)

## Engine / Architecture

- [ ] Populate `EngineStatistics` with live data — tick rate, pool pressure, frame times, producer/consumer throughput (struct exists as placeholder)
- [ ] Multi-threaded simulation — wire real per-worker tick computation into `SimulationWorker.ExecuteTick()` (thread machinery and dispatch cycle are in place)
- [ ] Thread pinning on macOS — current `ThreadPinning` supports Windows/Linux; macOS needs `thread_policy_set` with `THREAD_AFFINITY_POLICY` for hint-based co-location
- [ ] Thread pinning for >64 cores — `SetThreadGroupAffinity` on Windows, larger `cpu_set_t` on Linux
- [ ] Multi-threaded rendering — parallel render workers within a `Render` call (architecture supports this; needs implementation)

## Testing

- [ ] Tests for production adapters (`SystemClock`, `ThreadWaiter`, `TaskSaveRunner`) if they gain non-trivial logic
- [ ] `WorldImage` standalone tests as it gains real state

## Quality / Tooling

- [ ] Consider `Directory.Build.props` for shared project settings as more projects are added

## Documentation

- [ ] Architecture diagram (the mermaid-style flow in `Engine.cs` comments could become a standalone doc)
- [ ] Onboarding notes for the threading model — the doc comments are thorough but scattered across files

---

# Completed

## Engine / Architecture

- [x] Observe `Task.Run` results in `TaskSaveRunner` — save exceptions are now captured via `ConcurrentQueue<SaveEvent>` and surfaced through `EngineStatus` in the `RenderFrame` ([ea8ae2d](https://github.com/CyrusNajmabadi/Fabrica/commit/ea8ae2d))
- [x] One-tick-behind interpolation — consumption loop holds two snapshots, computes interpolation timing, renderer blends between real simulation states ([d1c4c9a](https://github.com/CyrusNajmabadi/Fabrica/commit/d1c4c9a))
- [x] Snapshot lifetime contract — renderers must not store snapshot refs beyond the Render call; documented in `IRenderer` and `RenderFrame` ([d1c4c9a](https://github.com/CyrusNajmabadi/Fabrica/commit/d1c4c9a))
- [x] Move `TickNumber` and `PublishTimeNanoseconds` from `WorldImage` to `WorldSnapshot` — publication metadata belongs on the snapshot, not the world state ([d1c4c9a](https://github.com/CyrusNajmabadi/Fabrica/commit/d1c4c9a))
- [x] Tighten type safety — mutable public fields on `WorldSnapshot` replaced with private-set properties; `WorldImage.Reset()` renamed to `ResetForPool()` ([d1c4c9a](https://github.com/CyrusNajmabadi/Fabrica/commit/d1c4c9a))
- [x] ObjectPool redesign — growable `Stack<T>`-backed pool; `Rent()` always returns (allocates on demand), `Return()` always accepts; pre-allocation preserved for cache warmth ([032a1dc](https://github.com/CyrusNajmabadi/Fabrica/commit/032a1dc))
- [x] Time-based backpressure — tick-epoch gap measured in nanoseconds with a 100ms low water mark (soft exponential delay) and a 2s hard ceiling (simulation blocks until consumption catches up); replaces pool-availability-based throttling ([032a1dc](https://github.com/CyrusNajmabadi/Fabrica/commit/032a1dc))
- [x] SimulationWorker stub — design placeholder documenting per-worker pools, created-nodes list for deferred ref-counting, and the threading contract for future multi-threaded simulation ([032a1dc](https://github.com/CyrusNajmabadi/Fabrica/commit/032a1dc))
- [x] Save overlap — analyzed and confirmed as a non-issue: `NextSaveAtTick` is set to 0 before dispatch and only rescheduled in the save task's `finally` block, so no second save can trigger while one is in flight

## Testing

- [x] True concurrency stress tests — 5 real multi-threaded tests (throughput, backpressure, shutdown, worker lifecycle, save pinning) with hard invariant checks ([e23657d](https://github.com/CyrusNajmabadi/Fabrica/commit/e23657d))
- [x] Backpressure adaptation tests — 7 deterministic multi-phase tests verifying the feedback loop adapts to matched rates, slow consumption, catch-up, re-engagement, exponential delays, hard ceiling, and bounded gap ([d697ce8](https://github.com/CyrusNajmabadi/Fabrica/commit/d697ce8))
- [x] xUnit v3 upgrade — migrated from xUnit 2.9.3 to xUnit v3 3.2.2 with aggressive parallel algorithm ([6057e02](https://github.com/CyrusNajmabadi/Fabrica/commit/6057e02))
- [x] Consolidate duplicate test infrastructure — shared `TestDoubles.cs` with `Test`-prefixed interface implementations, unified `WaiterState` families, 15 new MemorySystem/ObjectPool tests ([9a4b2bc](https://github.com/CyrusNajmabadi/Fabrica/commit/9a4b2bc))

## Quality / Tooling

- [x] `.editorconfig` for consistent formatting — rules for `var`, expression bodies, braces, `this.` qualification, naming; all elevated to warnings ([8b0d326](https://github.com/CyrusNajmabadi/Fabrica/commit/8b0d326), [cc97720](https://github.com/CyrusNajmabadi/Fabrica/commit/cc97720))
- [x] Coverage tooling — Coverlet collector added to test project; coverage uploaded to Codecov with badge on README ([7fe7d64](https://github.com/CyrusNajmabadi/Fabrica/commit/7fe7d64), [1ca02ed](https://github.com/CyrusNajmabadi/Fabrica/commit/1ca02ed))
- [x] CI pipeline — GitHub Actions workflow with separate build and test checks; coverage summary in run page ([7fe7d64](https://github.com/CyrusNajmabadi/Fabrica/commit/7fe7d64), [1ca02ed](https://github.com/CyrusNajmabadi/Fabrica/commit/1ca02ed))
