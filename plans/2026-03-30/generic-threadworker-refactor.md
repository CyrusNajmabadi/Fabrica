---
name: Generic ThreadWorker Refactor
overview: Extract a generic `ThreadWorker<TState, TExecutor>` and `WorkerGroup<TState, TExecutor>` from the existing `SimulationWorker`/`Simulator`, then implement multi-threaded rendering by reusing the same infrastructure with render-specific executor and state types.
todos:
  - id: generic-infra
    content: Create IThreadExecutor<TState>, ThreadWorker<TState, TExecutor>, WorkerGroup<TState, TExecutor>
    status: completed
  - id: simulation-domain
    content: Create SimulationTickState, SimulationExecutor; refactor Simulator to use WorkerGroup; delete SimulationWorker.cs
    status: completed
  - id: render-domain
    content: Create RenderDispatchState, RenderExecutor, RenderWorkerResources, RenderCoordinator
    status: completed
  - id: wire-integration
    content: Wire RenderCoordinator into ConsumptionLoop and Engine
    status: completed
  - id: build-test
    content: Build, run tests, fix any issues
    status: completed
  - id: update-todo
    content: Update TODO.md
    status: completed
isProject: false
---

# Generic ThreadWorker Refactor + Multi-threaded Rendering

## Core Idea

The existing `SimulationWorker` mixes two concerns: thread machinery (park/signal loop, shutdown, pinning) and domain work (`ExecuteTick`, images, resources). Extract the machinery into generic types, then reuse them for both simulation and rendering.

## New Generic Types

### `IThreadExecutor<TState>` (new file)

```csharp
interface IThreadExecutor<TState> where TState : struct
{
    void Prepare();
    void Execute(in TState state, CancellationToken cancellationToken);
}
```

### `ThreadWorker<TState, TExecutor>` (new file, replaces `SimulationWorker.cs`)

- Owns: `Thread`, go/done `AutoResetEvent`s, `volatile bool _shutdown`, core index
- Stores `TExecutor _executor` (by value) and `TState _state`
- `ThreadLoop()`: pin thread, park/signal loop, calls `_executor.Execute(in _state, ct)`
- Exposes: `ref TExecutor Executor`, `State` setter, `CancellationToken` setter, `Signal()`, `DoneEvent`, `Shutdown()`, `Join()`, `Cleanup()`

### `WorkerGroup<TState, TExecutor>` (new file, extracts dispatch cycle from `Simulator`)

- Owns: `ThreadWorker<TState, TExecutor>[]`, `WaitHandleBatch`
- `Dispatch(TState state, CancellationToken ct)`: prepare all → set state/CT → signal all → waitAll
- Exposes workers array for post-join access (e.g. `Simulator.CollectCreatedNodes`)
- `IDisposable`: shutdown → join → cleanup all workers
- Constructor takes a `Func<int, TExecutor>` factory + worker count + thread name prefix + core index offset

## Simulation Domain Types

### `SimulationTickState` (new file)

```csharp
readonly struct SimulationTickState { WorldImage PreviousImage; WorldImage NextImage; }
```

### `SimulationExecutor` (new file)

```csharp
struct SimulationExecutor : IThreadExecutor<SimulationTickState>
{
    internal readonly WorkerResources Resources;
    void Prepare() => Resources.PrepareForTick();
    void Execute(in SimulationTickState state, CancellationToken ct) { /* TODO */ }
}
```

### `Simulator` (modify) — becomes a thin wrapper around `WorkerGroup<SimulationTickState, SimulationExecutor>`

- `AdvanceTick` calls `_group.Dispatch(...)` then walks workers for `CollectCreatedNodes`

## Rendering Domain Types

### `RenderDispatchState` (new file)

```csharp
readonly struct RenderDispatchState { RenderFrame Frame; }
```

### `RenderWorkerResources` (new file) — stub, mirrors `WorkerResources`

### `RenderExecutor` (new file)

```csharp
struct RenderExecutor : IThreadExecutor<RenderDispatchState>
{
    internal readonly RenderWorkerResources Resources;
    void Prepare() => Resources.PrepareForFrame();
    void Execute(in RenderDispatchState state, CancellationToken ct) { /* TODO */ }
}
```

### `RenderCoordinator` (new file) — thin wrapper around `WorkerGroup<RenderDispatchState, RenderExecutor>`

## Integration

- `**ConsumptionLoop**`: accept optional `RenderCoordinator?`; if present, dispatch frame through it instead of calling `_renderer.Render` directly
- `**Engine**`: own and dispose `RenderCoordinator`; `Create()` takes optional render worker count; pass core index offset = simulation worker count so render threads pin to different cores
- `**SimulationWorker.cs**`: DELETE (fully replaced by generic `ThreadWorker` + `SimulationExecutor`)

## Test Impact

- All tests that do `new Simulator(N)` continue to work — `Simulator`'s public API (`AdvanceTick`, `WorkerCount`, `Dispose`) is unchanged
- `ConcurrencyStressTests` uses `Simulator` by name — no changes needed
- No test references `SimulationWorker` directly

## Files Changed

- **Delete**: `SimulationWorker.cs`
- **New**: `IThreadExecutor.cs`, `ThreadWorker.cs`, `WorkerGroup.cs`, `SimulationTickState.cs`, `SimulationExecutor.cs`, `RenderDispatchState.cs`, `RenderExecutor.cs`, `RenderWorkerResources.cs`, `RenderCoordinator.cs`
- **Modify**: `Simulator.cs`, `ConsumptionLoop.cs`, `Engine.cs`, `TODO.md`

