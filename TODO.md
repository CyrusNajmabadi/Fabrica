# TODO

Tracked work items for Fabrica. Roughly prioritized within each section.

## World State (the actual game)

- [ ] Belt state — item transport using the unit/speed system already defined in `SimulationConstants`
- [ ] Machine state — producers, consumers, inserters
- [ ] Persistent tree structure for `WorldImage` — share unchanged subtrees across ticks so memory scales with changes-per-tick, not total world size (mentioned in `WorldImage` doc comment)
- [ ] Actual world advance logic in `SimulationLoop.Tick` (currently a TODO)

## Engine / Architecture

- [ ] Populate `EngineStatistics` with live data — tick rate, pool pressure, frame times, producer/consumer throughput (struct exists as placeholder)
- [ ] Consider what happens when saves are slow enough to overlap with the next save interval (save-in-flight already prevented, but worth thinking about as real save logic arrives)
- [ ] Multi-threaded simulation — worker pool for tick computation (architecture already supports this; needs implementation)
- [ ] Multi-threaded rendering — parallel render workers within a `Render` call (architecture supports this; needs implementation)

## Testing

- [ ] True concurrency stress tests — run simulation and consumption on real threads and verify invariants hold (current tests are all single-threaded stepping)
- [ ] Consolidate duplicate test infrastructure — `RecordingWaiter`, clock types, and context builders are redefined across multiple test files
- [ ] Tests for production adapters (`SystemClock`, `ThreadWaiter`, `TaskSaveRunner`) if they gain non-trivial logic
- [ ] Broader `MemorySystem` unit tests — direct coverage for `ReturnImage` reset behavior, edge cases beyond constructor validation
- [ ] `ObjectPool` edge case tests — double-return, capacity boundary conditions
- [ ] `WorldImage` standalone tests as it gains real state

## Quality / Tooling

- [ ] Coverage tooling — no Coverlet or equivalent configured; would help identify gaps as the codebase grows
- [ ] CI pipeline (GitHub Actions or similar)
- [ ] `.editorconfig` for consistent formatting
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
