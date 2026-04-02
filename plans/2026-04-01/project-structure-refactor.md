---
name: Project structure refactor
overview: Extract the generic pipeline/threading/memory infrastructure into Fabrica.Pipeline, move simulation+rendering+hosting into Fabrica.Engine, and create Fabrica.ConsoleApp as the executable — with clean public API boundaries and no IVT between production libraries.
todos:
  - id: delete-stale
    content: Delete stale Simulation/ and Simulation.Tests/ directories
    status: completed
  - id: scaffold-projects
    content: Create src/Fabrica.Pipeline/, src/Fabrica.Engine/, src/Fabrica.ConsoleApp/ with .csproj files and project references
    status: completed
  - id: pipeline-config
    content: Introduce PipelineConfiguration to replace SimulationConstants references in pipeline code
    status: completed
  - id: move-pipeline
    content: Move Pipeline, Threading, Memory files + Host<...> into Fabrica.Pipeline with updated namespaces
    status: completed
  - id: move-engine
    content: Move Simulation, Rendering, World, Hosting files + SimulationEngine factory into Fabrica.Engine
    status: completed
  - id: move-consoleapp
    content: Move Program.cs + ConsoleHost files into Fabrica.ConsoleApp
    status: completed
  - id: visibility
    content: Flip cross-boundary types to public; ensure TestAccessors stay internal
    status: completed
  - id: move-tests
    content: Move tests to tests/Fabrica.Tests/ with updated references and namespaces
    status: completed
  - id: update-sln
    content: Update Fabrica.slnx with all new project paths
    status: completed
  - id: build-test-format
    content: Build, run all tests, dotnet format, verify CI-clean
    status: completed
isProject: false
---

# Project Structure Refactor

## Target layout

```
Fabrica/
├── src/
│   ├── Fabrica.Pipeline/       (core lib — generic pipeline, threading, memory)
│   ├── Fabrica.Engine/         (sim + render + hosting + world — depends on Pipeline)
│   └── Fabrica.ConsoleApp/     (exe — depends on Engine)
├── tests/
│   └── Fabrica.Tests/          (one test project for now — refs all three)
├── docs/
├── .cursor/
├── Fabrica.sln
└── ...
```

## What goes where

### Fabrica.Pipeline (class library, no exe)

The generic, payload-agnostic infrastructure. Everything here is parameterized over `TPayload` — it knows nothing about `WorldImage`, simulation, or rendering.

**From current `Engine/Pipeline/`** — all files move as-is:

- `BaseProductionLoop.cs` and all its partials (ChainNode, NodeMutation, ChainTestAccessor, ChainNodeAllocator, PrivateChainNode)
- `ProductionLoop.cs` and partials (SimulationPressure, TestAccessor)
- `ConsumptionLoop.cs` and partials (DeferredConsumerScheduler, TestAccessor)
- `SharedPipelineState.cs`
- `PinnedVersions.cs`
- Interfaces: `IProducer.cs`, `IConsumer.cs`, `IDeferredConsumer.cs`, `IPinOwner.cs`

**From current `Engine/Threading/`** — all files:

- `WorkerGroup.cs` and partials (ThreadWorker, WaitHandleBatch, ThreadPinning, TestAccessor)
- `ThreadPinningNative.cs`
- Interfaces: `IClock.cs`, `IWaiter.cs`, `IThreadExecutor.cs`
- Concrete adapters: `SystemClock.cs`, `ThreadWaiter.cs`

**From current `Engine/Memory/`** — all files:

- `ObjectPool.cs`, `IAllocator.cs`

**From current `Engine/`**:

- `EngineStatus.cs` (used by `RenderFrame` which is in Engine, but `EngineStatus` is a plain data type with no domain dependencies — could go either way)

**Key concern — `SimulationConstants`**: Currently `ProductionLoop`, `SimulationPressure`, and `ConsumptionLoop` reference `SimulationConstants` directly for tick duration, backpressure thresholds, render interval, and idle yield. These need to become **constructor parameters** (or a config struct) so the pipeline layer has no dependency on simulation-specific constants. This is the most significant code change in the refactor.

Constants that must move out of Pipeline:

- `TickDurationNanoseconds` — used by `ProductionLoop` (tick accumulator, pressure gap calc) and `RenderConsumer`
- `PressureLowWaterMarkNanoseconds`, `PressureHardCeilingNanoseconds`, `PressureBucketCount`, `PressureBaseDelayNanoseconds`, `PressureMaxDelayNanoseconds` — used by `SimulationPressure`
- `IdleYieldNanoseconds` — used by `ProductionLoop`
- `RenderIntervalNanoseconds` — used by `ConsumptionLoop`

Proposal: introduce a `PipelineConfiguration` record (or readonly struct) in Fabrica.Pipeline that bundles these, passed through constructors. `SimulationConstants` stays in Fabrica.Engine and constructs the config.

**Namespace**: `Fabrica.Pipeline`, `Fabrica.Pipeline.Threading`, `Fabrica.Pipeline.Memory`

**IVT**: only to `Fabrica.Tests`. No IVT to Fabrica.Engine or Fabrica.ConsoleApp.

**Visibility**: types consumed by Fabrica.Engine must become `public`. Internal types (TestAccessors, NodeMutation, PrivateChainNode, etc.) stay `internal`.

### Fabrica.Engine (class library, no exe)

Domain-specific simulation, rendering, world state, and the `Host` orchestrator. Depends on `Fabrica.Pipeline`.

**From current `Engine/Simulation/`** — all files:

- `SimulationCoordinator.cs`, `SimulationExecutor.cs`, `SimulationProducer.cs`, `SimulationTickState.cs`, `WorkerResources.cs`

**From current `Engine/Rendering/`** — all files:

- `RenderCoordinator.cs`, `RenderExecutor.cs`, `RenderConsumer.cs`, `RenderFrame.cs`, `RenderDispatchState.cs`, `RenderWorkerResources.cs`, `IRenderer.cs`

**From current `Engine/World/`**:

- `WorldImage.cs`

**From current `Engine/Hosting/`**:

- `Host.cs` (the generic `Host<...>` class + `SimulationEngine` factory)

**From current `Engine/`**:

- `SimulationConstants.cs`
- `EngineStatus.cs` (if we decide it belongs here instead of Pipeline — see open question below)

**Namespace**: `Fabrica.Engine`, `Fabrica.Engine.Simulation`, `Fabrica.Engine.Rendering`, `Fabrica.Engine.World`, `Fabrica.Engine.Hosting`

**IVT**: only to `Fabrica.Tests`. No IVT to Fabrica.ConsoleApp.

### Fabrica.ConsoleApp (exe)

Just the entry point. Depends on `Fabrica.Engine`.

**From current `Engine/`**:

- `Program.cs`

**From current `Engine/Hosting/ConsoleHost/`**:

- `ConsoleRenderer.cs`, `ConsoleSaveConsumer.cs`

**Namespace**: `Fabrica.ConsoleApp`

**IVT**: only to `Fabrica.Tests` (if needed). No IVT to any other production project.

### Fabrica.Tests (one test project, references all three)

All existing test files move here with updated namespaces/usings. Structure stays the same for now — splitting into per-project test libraries is a follow-up.

## Open question: `EngineStatus`

`EngineStatus` is currently a pure data struct with no dependencies. It's referenced only by `RenderFrame` and `RenderConsumer` (both in Engine). It could live in either layer:

- **Pipeline**: if we anticipate the pipeline itself populating diagnostics in the future
- **Engine**: since it's currently only used by rendering types

Recommendation: keep it in **Fabrica.Engine** for now since it has no pipeline dependencies and is only consumed by rendering code.

## Open question: `Host<...>`

`Host<TPayload, TProducer, TConsumer, TClock, TWaiter>` is fully generic — it just creates threads and runs the production/consumption loops. It could arguably live in Pipeline. However, the `SimulationEngine` factory in the same file is domain-specific. Options:

- Split the file: `Host<...>` in Pipeline, `SimulationEngine` factory in Engine
- Keep both in Engine

Recommendation: **split** — `Host<...>` is pure pipeline infrastructure and should be in Fabrica.Pipeline. `SimulationEngine` moves to Fabrica.Engine.

## Execution order

1. Delete stale `Simulation/` and `Simulation.Tests/` directories (build artifacts only)
2. Create `src/Fabrica.Pipeline/`, `src/Fabrica.Engine/`, `src/Fabrica.ConsoleApp/` project scaffolds with `.csproj` files
3. Introduce `PipelineConfiguration` to replace `SimulationConstants` references in pipeline code
4. Move files into the new projects, updating namespaces and usings
5. Update `Fabrica.slnx` with the new project paths
6. Flip visibility: types that cross the Pipeline-to-Engine boundary become `public`; TestAccessors stay `internal`
7. Move tests to `tests/Fabrica.Tests/`, update project references
8. Update `Fabrica.slnx` with the new project paths
9. Build, test, format

