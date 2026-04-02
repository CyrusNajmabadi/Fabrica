---
name: Move pipeline tests down
overview: Move 7 test files from Engine.Tests to Pipeline.Tests, replacing SimulationProducer/WorldImage with WorkerGroup-backed test doubles that exercise the full threading infrastructure at the pipeline layer.
todos:
  - id: worker-doubles
    content: Create TestWorkerProducer, TestWorkerConsumer, SpinWorkExecutor in Pipeline.Tests/Helpers
    status: completed
  - id: move-rename
    content: git mv 7 files from Engine.Tests/Engine/ to Pipeline.Tests/Pipeline/ with new names
    status: completed
  - id: port-pressure
    content: Port PressureComputationTests (was SimulationPressureTests)
    status: completed
  - id: port-tick
    content: Port ProductionLoopTickTests (was SimulationLoopTickTests)
    status: completed
  - id: port-run
    content: Port ProductionLoopRunTests (was SimulationLoopRunTests)
    status: completed
  - id: port-additional
    content: Port ProductionLoopAdditionalTests (was SimulationLoopAdditionalTests)
    status: completed
  - id: port-harness
    content: Port PipelineHarnessTests (was LoopHarnessExampleTests)
    status: completed
  - id: port-stress
    content: Port PipelineStressHarnessTests (was LoopStressHarnessTests)
    status: completed
  - id: port-backpressure
    content: Port BackpressureAdaptationTests
    status: completed
  - id: cleanup-engine
    content: "Clean up Engine.Tests: remove empty dirs, trim TestDoubles if possible"
    status: completed
  - id: verify
    content: Build, test, format — verify all clean
    status: completed
isProject: false
---

# Move Remaining Pipeline Tests to Fabrica.Pipeline.Tests

## New Test Doubles

Create worker-backed test producer and consumer in `tests/Fabrica.Pipeline.Tests/Helpers/` so pipeline stress/harness tests exercise `WorkerGroup` dispatch without depending on engine types.

`**TestWorkerProducer**` (struct, `IProducer<TestPayload>`):

- Owns an `ObjectPool<TestPayload, TestPayload.Allocator>` and a `WorkerGroup<EmptyWorkState, SpinWorkExecutor>`
- `Produce()` rents a payload from the pool, dispatches to the worker group, returns the payload
- `ReleaseResources()` returns to the pool
- Mirrors what `SimulationProducer` does: pool rent + coordinator dispatch + return

`**TestWorkerConsumer**` (struct, `IConsumer<TestPayload>`):

- Owns a `WorkerGroup<EmptyWorkState, SpinWorkExecutor>`
- `Consume()` dispatches to the worker group, exercising worker threads on the consumption side

`**SpinWorkExecutor**` (struct, `IThreadExecutor<EmptyWorkState>`):

- `Execute()` does `Thread.SpinWait(10)` — trivial but non-zero work to exercise the thread machinery

These go alongside the existing `TestNoOpConsumer` / `TestRecordingClock` etc. in `tests/Fabrica.Pipeline.Tests/Helpers/TestDoubles.cs`.

## Files to Move and Rename

All from `tests/Fabrica.Engine.Tests/Engine/` to `tests/Fabrica.Pipeline.Tests/Pipeline/`:


| Old name                           | New name                           | Rationale                                                                         |
| ---------------------------------- | ---------------------------------- | --------------------------------------------------------------------------------- |
| `SimulationPressureTests.cs`       | `PressureComputationTests.cs`      | Tests `ProductionLoop.SimulationPressure.ComputeDelay()` — a static pure function |
| `SimulationLoopTickTests.cs`       | `ProductionLoopTickTests.cs`       | Tests `ProductionLoop` tick/cleanup/pin mechanics                                 |
| `SimulationLoopRunTests.cs`        | `ProductionLoopRunTests.cs`        | Tests `ProductionLoop` run/accumulator/clock mechanics                            |
| `SimulationLoopAdditionalTests.cs` | `ProductionLoopAdditionalTests.cs` | Tests `ProductionLoop` pressure delays and edge cases                             |
| `LoopHarnessExampleTests.cs`       | `PipelineHarnessTests.cs`          | Tests full production+consumption loop integration                                |
| `LoopStressHarnessTests.cs`        | `PipelineStressHarnessTests.cs`    | Tests backpressure under sustained iteration                                      |
| `BackpressureAdaptationTests.cs`   | `BackpressureAdaptationTests.cs`   | Name already good                                                                 |


**Stays in Engine.Tests**: `ConcurrencyStressTests.cs` — genuinely tests `SimulationProducer` multi-threaded worker dispatch under real concurrency.

## Porting Each File

For every moved file:

- `WorldImage` -> `TestPayload`, `WorldImage.Allocator` -> `TestPayload.Allocator`
- `SimulationProducer` -> `TestWorkerProducer` (with `workerCount` param preserved)
- `SimulationConstants.X` -> `TestPipelineConfiguration.X` (or named constants from the config)
- `SimulationPressure` type alias updated to use `TestWorkerProducer` + `TestPayload`
- Namespace -> `Fabrica.Pipeline.Tests.Pipeline`
- Usings cleaned up (no `Fabrica.Engine.`*)

Tests that need a recording consumer (e.g. `PipelineHarnessTests`) will keep local `TestRecordingConsumer` structs (already inline in each file). Tests that just need throughput (stress/backpressure) can use `TestWorkerConsumer` or `TestNoOpConsumer`.

## Engine.Tests Cleanup

After the move, `Engine.Tests` will contain only:

- `Engine/ConcurrencyStressTests.cs`
- `Helpers/TestDoubles.cs`

Check whether `TestDoubles.cs` can be trimmed (e.g., if `TestNoOpConsumer`, `TestChainHarness` etc. are no longer used by `ConcurrencyStressTests`).

## Verification

- `dotnet build` with zero warnings
- `dotnet test` — all 112 tests pass
- `dotnet format --verify-no-changes` — clean
- No `Fabrica.Engine` imports in any `Fabrica.Pipeline.Tests` file

