---
name: Concurrency Stress Tests
overview: Add real multi-threaded concurrency stress tests to the engine that exercise the volatile visibility contracts, epoch-based reclamation under contention, worker signal/park lifecycle, backpressure engagement, and graceful shutdown — all with true pass/fail invariant checks.
todos:
  - id: stress-infra
    content: "Create stress test infrastructure: StressClock, ShortWaiter, InvariantCheckingRenderer, StressMetrics, thread-management helpers"
    status: completed
  - id: test-throughput
    content: "Test 1: SustainsHighThroughput_NoDeadlocks — real threads, 2s run, monotonic ticks, no exceptions"
    status: completed
  - id: test-backpressure
    content: "Test 2: BackpressureEngages_WhenConsumptionIsSlowedDown — slow renderer, verify bounded gap"
    status: completed
  - id: test-shutdown
    content: "Test 3: GracefulShutdown_UnderLoad — cancel under load, verify clean exit within timeout"
    status: completed
  - id: test-workers
    content: "Test 4: WorkerSignalParkCycle — high worker count, many ticks, no races"
    status: completed
  - id: test-save-pin
    content: "Test 5: SavePinning_AcrossThreadBoundaries — frequent saves with real threads"
    status: completed
  - id: ci-trait
    content: Add [Trait] category for stress tests, verify CI passes
    status: completed
isProject: false
---

# Concurrency Stress Tests

## Problem

All existing tests step through loops single-threaded via `TestAccessor`. Nothing exercises the actual concurrency contracts: volatile publish/acquire on `SharedState`, epoch reclamation racing with consumption reads, worker signal/park with real threads, or backpressure engaging from real timing mismatches.

## Approach

Add a new test file `Simulation.Tests/Engine/ConcurrencyStressTests.cs` with real multi-threaded tests. Each test creates real `SimulationLoop` + `ConsumptionLoop` instances, runs them on dedicated threads (mirroring `Engine.Run`), and checks invariants.

### Test Infrastructure Needed

A small set of struct implementations purpose-built for stress tests, defined inside the test class (matching the existing pattern — each test file owns its fakes):

- `**StressClock**` — wraps `Stopwatch` for real wall-clock nanoseconds
- `**ShortWaiter**` — actually sleeps (using `Thread.Sleep` + `CancellationToken`), but can be configured for shorter/longer sleeps to amplify scheduling pressure
- `**InvariantCheckingRenderer**` — the key piece. Called on every consumption frame. Holds a reference to a shared `StressMetrics` class and on each `Render` call:
  - Increments frame counter (for "did it run?" checks)
  - Records the tick numbers of `Previous`/`Current` 
  - Asserts `Current` is never null after first frame
  - Asserts tick numbers are monotonically non-decreasing
  - Tracks max tick gap between simulation and consumption (via tick numbers observed)
- `**StressMetrics**` — thread-safe accumulator class. Uses `Interlocked` for counters that both threads might touch, plain fields for consumption-thread-only data (since the renderer runs on the consumption thread)
- `**CountingSaveRunner` / `NoOpSaver**` — for save-exercising scenarios

### Thread management pattern

Each test manually creates `SimulationLoop`, `ConsumptionLoop`, `SharedState`, `MemorySystem`, and `Simulator`, then runs them on real threads with exception capture:

```csharp
Exception? simulationException = null;
Exception? consumptionException = null;

var simThread = new Thread(() =>
{
    try { simLoop.Run(cts.Token); }
    catch (OperationCanceledException) { }
    catch (Exception ex) { Volatile.Write(ref simulationException, ex); }
});
// similar for consumption
```

After `Join`, check for captured exceptions before asserting metrics.

### Test Cases

**Test 1: SustainsHighThroughput_NoDeadlocks**

- No-op tick work, no-op render, 1+ workers, run for ~2 seconds
- Assert: frames rendered > 0, ticks observed are monotonic, no exceptions, both threads exited

**Test 2: BackpressureEngages_WhenConsumptionIsSlowedDown**

- Fast simulation, renderer adds artificial delay (~50ms per frame, 3x the 16ms budget)
- Run for ~3 seconds
- Assert: system stays alive, tick-epoch gap remains bounded (pool didn't blow up), both threads exited cleanly
- This is a behavioral assertion, not a timing assertion — it proves backpressure prevented unbounded growth

**Test 3: GracefulShutdown_UnderLoad**

- Both threads running at full speed, cancel after ~1 second
- Assert: both threads exit within 5 seconds (timeout guard), no deadlock, `Simulator.Dispose` completes (workers shut down)

**Test 4: WorkerSignalParkCycle_SurvivesManyTicks**

- Multiple workers (4+), run thousands of ticks
- Assert: no `AutoResetEvent` races (workers all complete each tick), no deadlock, clean shutdown
- This is really tested implicitly by Tests 1-3 running with workerCount > 1, but worth an explicit high-worker-count case

**Test 5: SavePinning_AcrossThreadBoundaries** (if save path is exercisable)

- Configure `NextSaveAtTick = 1` so saves fire immediately
- Use a saver that sleeps briefly (simulating I/O)
- Run for a few seconds
- Assert: saves completed (checked via metrics), no use-after-free on pinned snapshots, `PinnedVersions` is empty at shutdown

### What constitutes "passing"

Every assertion is a hard invariant — no timing-sensitive "was this fast enough?" checks. The tests answer: "did the system maintain its safety contracts under real concurrent execution?" If any invariant is violated, it's a real bug.

### CI considerations

These tests use real threads and real time, so they take seconds (not milliseconds). Tag them with `[Trait("Category", "Stress")]` so they can be filtered in CI if needed. They should be reliable on any hardware — the invariants don't depend on throughput numbers, only on correctness.

## Follow-up (not in this pass)

- Separate `Simulation.Benchmarks` console project for throughput scenarios and BenchmarkDotNet microbenchmarks
- Consolidate duplicated test infrastructure (clocks, waiters, etc.) across test files

