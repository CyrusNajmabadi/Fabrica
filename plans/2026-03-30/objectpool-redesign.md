---
name: ObjectPool redesign
overview: Redesign ObjectPool to always allocate (no null returns, growable), switch backpressure to tick-epoch gap, and stub out the SimulationWorker concept as a design placeholder for future multi-threaded tick computation.
todos:
  - id: pool-semantics
    content: "Redesign ObjectPool: List-backed, always-allocate Rent(), always-accept Return(), keep pre-allocation and DEBUG thread assertion"
    status: completed
  - id: memory-system
    content: "Update MemorySystem: non-nullable Rent returns, remove pool-size-must-be-multiple constraint"
    status: completed
  - id: backpressure
    content: Rewrite backpressure to use tick-epoch gap instead of pool availability
    status: completed
  - id: simplify-tick
    content: "Simplify SimulationLoop.Tick(): remove retry loop, direct allocation"
    status: completed
  - id: worker-stub
    content: Create SimulationWorker stub with doc comments for future multi-threaded design
    status: completed
  - id: update-tests
    content: "Update tests: starvation scenario, simplified Tick, non-nullable Rent helpers"
    status: completed
  - id: build-test
    content: Build and run all tests
    status: completed
isProject: false
---

# ObjectPool Redesign and SimulationWorker Stub

## 1. ObjectPool semantics change

`[Simulation/Memory/ObjectPool.cs](Simulation/Memory/ObjectPool.cs)`

- **Rent() always returns a value** — return type becomes `T` (non-nullable). If the internal list is empty, allocate `new T()`.
- **Return() always accepts** — the backing store becomes a `List<T>` (or `Stack<T>`) that grows as needed. No capacity overflow assertions.
- **Pre-allocation stays** — constructor still creates an initial batch of objects for cache warmth and to reduce early GC pressure. This becomes the "preferred size," not a hard cap.
- **Single-threaded per instance** — the DEBUG thread-ID assertion stays. Each pool instance is owned by one thread.
- **Remove `Capacity` property** — no longer meaningful. Keep `Available` (number of pooled objects ready for reuse). Add `TotalAllocated` or similar if useful for diagnostics.

## 2. Backpressure redesign

`[Simulation/Engine/SimulationLoop.cs](Simulation/Engine/SimulationLoop.cs)` — `ApplyPressureDelay`

- **Signal: `_currentTick - _shared.ConsumptionEpoch`** — measures how far simulation is ahead of consumption. This is the actual problem: if consumption can't keep up, we're producing faster than we're reclaiming.
- **Low water mark** — below this, no delay. Simulation runs freely. Some slack is expected and healthy.
- **High water mark / exponential curve** — above the low mark, exponentially increasing delay (same shape as today's `SimulationPressure.ComputeDelay`, but driven by outstanding count instead of pool availability).
- **Remove pool-availability-based pressure** — `SnapshotsAvailable` / `SnapshotPoolCapacity` no longer drives throttling.

## 3. Simplify Tick()

`[Simulation/Engine/SimulationLoop.cs](Simulation/Engine/SimulationLoop.cs)` — `Tick`

The retry loop (`while (snapshot is null) { ... CleanupStaleSnapshots(); sleep; retry; }`) goes away entirely. `Rent()` always succeeds, so Tick becomes:

```csharp
private void Tick(...)
{
    WorldImage image = _memory.RentImage();
    WorldSnapshot snapshot = _memory.RentSnapshot();
    _currentTick++;
    // ... advance world state, initialize, publish ...
}
```

## 4. MemorySystem updates

`[Simulation/Memory/MemorySystem.cs](Simulation/Memory/MemorySystem.cs)`

- `RentImage()` and `RentSnapshot()` return non-nullable `WorldImage` and `WorldSnapshot`.
- Remove pool-size validation constraint (must be multiple of PressureBucketCount) — no longer relevant since backpressure isn't pool-based.
- Keep `ReturnImage` / `ReturnSnapshot` as-is (they call `ResetForPool()` and return to pool).

## 5. SimulationWorker stub

New file: `Simulation/Engine/SimulationWorker.cs`

A design placeholder that documents the intended multi-threaded simulation model:

- **Owns a per-worker `ObjectPool<T>`** for image tree nodes (future — no tree nodes exist yet)
- **Owns a created-nodes list** for deferred ref-counting (the main sim thread walks this after workers join)
- **Doc comments** explain the threading contract: exclusive pool access, deferred ref-count adjustment, no shared mutable state during tick computation
- Minimal code — just the class shell with fields and documentation. No thread management yet.

## 6. Test impact

- **LoopStressHarnessTests** — the starvation/recovery test (`RecoversFromSnapshotStarvation_AfterConsumptionAdvancesEpochDuringWait`) tests a scenario that can no longer happen (pool exhaustion). Rewrite to test the new backpressure signal (tick-epoch gap throttling).
- **SimulationLoopTickTests** — tests that assert on pool-empty retry behavior need restructuring. Tests for the simplified Tick() will be shorter.
- **ConsumptionLoopTests** — the `CreatePublishedSnapshot` helper creates snapshots manually via `Memory.RentSnapshot()`. Return type changes from nullable to non-nullable, simplifying these helpers.
- **SimulationPressure tests** (if any) — update to use tick-epoch gap inputs instead of available/capacity.

