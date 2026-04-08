---
name: Realistic saturation benchmark
overview: Create a multi-phase DAG benchmark with ~200 jobs per tick, each doing 20-50μs of real compute, to measure pipeline efficiency under realistic core saturation.
todos:
  - id: compute-job
    content: Create ComputeJob base class with calibrated hash-mixing work loop + node allocation
    status: completed
  - id: benchmark-class
    content: Create RealisticTickBenchmark with 4-phase DAG, warmup, and BDN attributes
    status: completed
  - id: calibrate
    content: Run quick calibration to find iteration count that gives ~30-40us per job
    status: completed
  - id: run-benchmark
    content: "Run benchmark: collect mean tick time, verify zero alloc, collect CPU trace"
    status: completed
  - id: analyze
    content: Analyze CPU trace, report hotspot breakdown, identify optimization opportunities
    status: completed
isProject: false
---

# Realistic Core-Saturation Benchmark

## Design

A benchmark modeling a simplified game tick with 4 sequential phases, each fanning out to many parallel jobs. Each job does real CPU work (hash-mixing over a small array) calibrated to ~30-40us, plus node allocation through our arena system. This gives ~200 jobs total with a realistic DAG shape and enough work per job that scheduling overhead is not the dominant cost.

### DAG shape

```
Phase 1: TriggerJob --> 64 EntityUpdateJobs (AI/decision, ~35us each)
Phase 2:            --> 64 PhysicsJobs      (movement/collision, ~35us each)
Phase 3:            --> 32 WorldUpdateJobs   (spatial index, ~30us each)  
Phase 4:            --> 16 SnapshotJobs      (build node tree, ~25us each)
                    -->  1 RootCollectorJob  (fan-in, trivial)
```

Each phase depends on the previous phase completing (fan-in then fan-out). Total: ~177 jobs. On a 16-core machine, each parallel phase has enough work to keep all cores busy.

### Job work simulation

- Each job receives a work array (`int[]` of 64 elements, fits in L1) and an iteration count
- The `Execute` body runs a tight hash-mixing loop: `for (i = 0..N) array[i % 64] = HashMix(array[i % 64], i)`
- Calibrate N so each job takes ~30-40us on the target machine (likely N ~ 2000-4000)
- Phase 4 (Snapshot) jobs also allocate ~10 BenchNodes each, exercising the arena path

### Benchmark class: `RealisticTickBenchmark`

- Pre-allocates all jobs once in `[GlobalSetup]`
- Each `[Benchmark]` iteration: reset, wire DAG, submit, merge, snapshot, release previous
- `[MemoryDiagnoser]` to verify zero steady-state allocation
- `[EventPipeProfiler]` to collect CPU traces

### Files to create/modify

All new files in `benchmarks/Fabrica.SampleGame.Benchmarks/Scale/`:

- **`ComputeJob.cs`** -- Base job class with configurable compute loop (hash-mixing on a shared work array). Subclasses set iteration count per phase.
- **`RealisticTickBenchmark.cs`** -- Benchmark class with 4-phase DAG wiring, warmup, and OneTick method.

No changes to existing production code.

### What we measure

1. **Mean tick time** -- raw throughput for a realistic workload
2. **Zero allocation** -- confirm steady state after warmup
3. **CPU trace** -- where is time actually spent (useful work vs. scheduling vs. merge vs. idle)
4. **Scaling** -- we can parameterize worker count to see how tick time changes with core count

This gives us the "so-so realistic" baseline to identify the highest-cost bottlenecks and lowest-hanging fruit for optimization.