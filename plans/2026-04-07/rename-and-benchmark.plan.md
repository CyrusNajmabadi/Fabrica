---
name: Rename and benchmark
overview: Rename Fabrica.Game to Fabrica.SampleGame across all projects, then create a BenchmarkDotNet project that validates zero steady-state allocations for the game pipeline.
todos:
  - id: rename
    content: Rename Fabrica.Game -> Fabrica.SampleGame (directories, csproj, slnx, InternalsVisibleTo, namespaces)
    status: completed
  - id: benchmark-project
    content: Create benchmarks/Fabrica.SampleGame.Benchmarks/ project with BenchmarkDotNet
    status: completed
  - id: steady-state-bench
    content: "Implement SteadyStateBenchmark: warm-up ticks + measured tick cycle with MemoryDiagnoser"
    status: completed
  - id: fix-allocs
    content: Identify and fix any steady-state allocations found by the benchmark
    status: completed
isProject: false
---

# Rename Fabrica.Game to Fabrica.SampleGame + Steady-State Benchmark

## Part 1: Rename (separate commit/PR)

Mechanical rename of `Fabrica.Game` -> `Fabrica.SampleGame` across three project groups:

**Directories to rename:**
- `src/Fabrica.Game/` -> `src/Fabrica.SampleGame/`
- `src/Fabrica.Game.ConsoleApp/` -> `src/Fabrica.SampleGame.ConsoleApp/`
- `tests/Fabrica.Game.Tests/` -> `tests/Fabrica.SampleGame.Tests/`

**Files to rename (inside each directory):**
- `Fabrica.Game.csproj` -> `Fabrica.SampleGame.csproj` (and similarly for ConsoleApp/Tests)

**References to update:**
- `Fabrica.slnx` — three project paths
- `InternalsVisibleTo` in `src/Fabrica.SampleGame/Fabrica.SampleGame.csproj`, `src/Fabrica.Core/Fabrica.Core.csproj`, `src/Fabrica.Engine/Fabrica.Engine.csproj` — change `Fabrica.Game.Tests` to `Fabrica.SampleGame.Tests`
- `ProjectReference` paths in `Fabrica.SampleGame.ConsoleApp.csproj` and `Fabrica.SampleGame.Tests.csproj`
- C# namespaces: `Fabrica.Game` -> `Fabrica.SampleGame`, `Fabrica.Game.Jobs` -> `Fabrica.SampleGame.Jobs`, `Fabrica.Game.Nodes` -> `Fabrica.SampleGame.Nodes`, `Fabrica.Game.Tests` -> `Fabrica.SampleGame.Tests`

No logic changes.

## Part 2: Benchmark project + large DAG steady-state benchmark

**New project:** `benchmarks/Fabrica.SampleGame.Benchmarks/`
- References `Fabrica.SampleGame` (and transitively `Fabrica.Core`)
- `[MemoryDiagnoser]` on all benchmarks to track allocations
- Add to `Fabrica.slnx`

**Benchmark: `SteadyStateBenchmark`**

The benchmark validates zero allocations in steady state by:

1. **GlobalSetup**: Create stores, worker pool, scheduler, tick state (same as `GameEngine.Create` but without the pipeline host). Run ~10 warm-up ticks (each: build job DAG, submit, merge, build snapshot slices, release previous snapshot). This forces all pools, arenas, slabs, and TLBs to reach their high-water marks.

2. **Benchmark method**: Run one full tick cycle:
   - Build job DAG (SpawnItems -> BuildBelts -> PlaceMachines)
   - Submit to scheduler (blocks until complete)
   - `coordinator.MergeAll()` + `BuildSnapshotSlice()` per type
   - Release the *previous* tick's snapshot (cascade-free decrements)
   - This mirrors exactly what `GameProducer.Produce` + `ReleaseResources` does

3. **What BenchmarkDotNet tells us**:
   - `[MemoryDiagnoser]` reports Gen0/Gen1/Gen2 collections and bytes allocated. After warm-up, we expect **0 bytes allocated** per iteration.
   - Mean/median/stddev of tick time gives throughput.

**Key consideration**: The current `SpawnItemsJob.Execute` allocates `new Handle<ItemNode>[Count]` on every tick. This will show up as a steady-state allocation. Similarly, `BuildBeltChainJob` and `PlaceMachinesJob` likely have similar patterns. We will discover and fix these as part of the benchmark work (this is exactly the point -- the benchmark finds allocation leaks).

**Stretch (same PR if straightforward)**: A second benchmark method with a larger scale (more items/belts/machines per tick) to stress the system.
