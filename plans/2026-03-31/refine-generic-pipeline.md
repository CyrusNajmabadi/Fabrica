---
name: Refine generic pipeline
overview: "Three architectural changes to the generic pipeline: (1) refactor ChainNode to hold a generic TPayload so the producer just creates payloads, (2) replace save-specific machinery in ConsumptionLoop with a general-purpose deferred consumer pattern, and (3) clean up IConsumer to not leak save status and rename SimulationConsumer to RenderConsumer."
todos:
  - id: refactor-chain-node
    content: Refactor ChainNode<TSelf> (CRTP) into ChainNode<TPayload> with a Payload property; delete WorldSnapshot class
    status: completed
  - id: refactor-producer
    content: Change IProducer<TNode> to IProducer<TPayload> that returns payloads; update SimulationProducer and ProductionLoop
    status: completed
  - id: deferred-consumer
    content: Create IDeferredConsumer<TPayload> interface with auto-pin/unpin lifecycle
    status: pending
  - id: extract-save
    content: Remove all save machinery from ConsumptionLoop; create SaveConsumer as an IDeferredConsumer<WorldImage>
    status: pending
  - id: clean-iconsumer
    content: Remove SaveStatus from IConsumer; rename SimulationConsumer to RenderConsumer
    status: completed
  - id: update-tests
    content: Update all test files for new type signatures and removed save infrastructure from loop
    status: completed
  - id: build-test-pr
    content: Build, test, format, commit, push, create PR
    status: completed
isProject: false
---

# Refine Generic Producer/Consumer Pipeline

## 1. Refactor ChainNode to `ChainNode<TPayload>` (producer creates payloads, not nodes)

Currently `ChainNode<TSelf>` uses CRTP and `WorldSnapshot` inherits from it. The producer receives a `WorldSnapshot` and sets `node.Image` directly. The user wants the producer to only create payloads (e.g. `WorldImage`) — the loop wraps them in chain nodes.

**New model:**

- `ChainNode<TPayload>` is a concrete (non-abstract, sealed) class holding `TPayload Payload` plus all chain mechanics (sequence, next, ref-count, publish time, iterator)
- `WorldSnapshot` goes away as a class — it becomes a type alias concept: `ChainNode<WorldImage>` IS the snapshot
- `IProducer<TPayload>` creates `TPayload` values; the loop puts them into chain nodes
- `OnReleased` is no longer needed as a virtual — the loop just nulls `Payload` directly when freeing

**Key file changes:**

- `[Simulation/World/ChainNode.cs](Simulation/World/ChainNode.cs)`: Remove CRTP, become `ChainNode<TPayload>` with `public TPayload Payload` property. Remove `abstract`, remove `OnReleased` virtual. Add `internal void SetPayload(TPayload payload)` and `internal void ClearPayload()`.
- `[Simulation/World/WorldSnapshot.cs](Simulation/World/WorldSnapshot.cs)`: Delete or convert to a type alias / static helper. Tests and domain code use `ChainNode<WorldImage>` directly. If domain aliases are desired, a static class `WorldSnapshot` could provide helpers like `int TickNumber(this ChainNode<WorldImage> node) => node.SequenceNumber`.
- `[Simulation/Engine/Pipeline/IProducer.cs](Simulation/Engine/Pipeline/IProducer.cs)`: Change to `IProducer<TPayload>` with `TPayload Bootstrap(...)`, `TPayload Produce(TPayload current, ...)`, `void ReleaseResources(TPayload payload)`.
- `[Simulation/Engine/ProductionLoop.cs](Simulation/Engine/ProductionLoop.cs)`: Now `ProductionLoop<TPayload, TProducer, ...>`. Uses `ObjectPool<ChainNode<TPayload>>`. Calls `producer.Bootstrap()` to get initial payload, assigns it to the node.
- `[Simulation/Engine/Simulation/SimulationProducer.cs](Simulation/Engine/Simulation/SimulationProducer.cs)`: `IProducer<WorldImage>` — returns `WorldImage` from `Bootstrap`/`Produce`, returns image to pool in `ReleaseResources`.

## 2. Replace save machinery with generic deferred consumers

Remove all save-specific code from `ConsumptionLoop`. Instead, introduce `IDeferredConsumer<TPayload>` — a slow consumer that the loop auto-pins before calling and auto-unpins when its async work completes.

`**IDeferredConsumer<TPayload>` interface:**

```csharp
internal interface IDeferredConsumer<TPayload>
{
    Task<long> ConsumeAsync(TPayload payload, int sequenceNumber, CancellationToken ct);
}
```

- `ConsumeAsync` returns `Task<long>` where the `long` is the next-run-time in nanoseconds.
- No property polling — the consumer tells the loop when it wants to run next by returning the value from its async work.

**Scheduling via min-heap:**

The consumption loop maintains a `PriorityQueue<IDeferredConsumer<TPayload>, long>` (or `SortedSet<(long nextRun, int index)>`) keyed by next-run-time. Each frame:

1. Peek the heap head — O(1). If `clock.Now < head.nextRun`, skip (nothing due).
2. Pop all due entries. For each:
  - If a prior task for this consumer is still in flight, skip (re-enqueue with same time).
  - Auto-pin `latest.SequenceNumber`.
  - Call `consumer.ConsumeAsync(latest.Payload, latest.SequenceNumber, ct)` — returns `Task<long>`.
  - Track the in-flight task.
3. Drain completed tasks: for each finished task, auto-unpin, read the returned `long` (next-run-time), re-insert into the heap.

This means the hot path (frames where nothing is due) is a single O(1) time comparison against the heap head — no virtual calls, no iteration over consumers.

**Initial scheduling:** Each deferred consumer is registered with an initial `nextRunAtNanoseconds` when the engine is constructed. The loop inserts `(initialNextRun, consumer)` into the heap at startup.

**Saving becomes one deferred consumer:**

```csharp
internal class SaveConsumer : IDeferredConsumer<WorldImage>
{
    public Task<long> ConsumeAsync(WorldImage payload, int seq, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            // actual save work
            Save(payload, seq);
            // return next run time: now + save interval
            return clock.NowNanoseconds + saveIntervalNanoseconds;
        }, ct);
    }
}
```

**Key file changes:**

- Create `[Simulation/Engine/Pipeline/IDeferredConsumer.cs](Simulation/Engine/Pipeline/IDeferredConsumer.cs)`
- `[Simulation/Engine/ConsumptionLoop.cs](Simulation/Engine/ConsumptionLoop.cs)`: Remove `TSaveRunner`, `TSaver`, `MaybeStartSave`, `RunSaveTask`, `DrainSaveEvents`, `SaveEvent` queue, `NextSaveAtTick`. Add `IDeferredConsumer<TPayload>[]` array with per-consumer heap-based scheduling and pin/task tracking.
- `[Simulation/Engine/SharedState.cs](Simulation/Engine/SharedState.cs)`: Remove `NextSaveAtTick` (it becomes internal to the save deferred consumer).
- Delete `[Simulation/Engine/ISaver.cs](Simulation/Engine/ISaver.cs)` and `[Simulation/Engine/ISaveRunner.cs](Simulation/Engine/ISaveRunner.cs)`
- `[Simulation/Engine/EngineStatus.cs](Simulation/Engine/EngineStatus.cs)`: `SaveStatus`/`SaveEvent` move to the save consumer or get removed from the generic pipeline level

## 3. Clean up IConsumer — remove SaveStatus, rename SimulationConsumer

- `[Simulation/Engine/Pipeline/IConsumer.cs](Simulation/Engine/Pipeline/IConsumer.cs)`: Remove `SaveStatus` parameter. The signature becomes `Consume(TPayload? previous, TPayload latest, long frameStartNanoseconds, CancellationToken ct)` — but wait, the consumer receives payloads now (not nodes). Actually, the consumer also needs the `ChainNode` for `PublishTimeNanoseconds` and the chain iterator. Let me reconsider: the consumer should receive `ChainNode<TPayload>` references since it needs publish timestamps for interpolation and chain access. So: `Consume(ChainNode<TPayload>? previous, ChainNode<TPayload> latest, long frameStartNanoseconds, CancellationToken ct)`.
- Rename `SimulationConsumer` to `RenderConsumer` — it owns the `RenderCoordinator` and `TRenderer` internally.
- If the render consumer needs save status for the `EngineStatus` display, it can observe it through a separate shared object (not through `IConsumer`).

## Open question: TPayload constraints

`ObjectPool<T>` requires `T : class, new()`. `ChainNode<TPayload>` is a class with `new()` (needs parameterless ctor). `TPayload` itself could be a class or struct. If struct, it lives inline in the node. If class, the pool manages nodes and the producer manages payload allocation separately. `WorldImage` is currently a class, so `ChainNode<WorldImage>` would hold a reference. This matches the current design.