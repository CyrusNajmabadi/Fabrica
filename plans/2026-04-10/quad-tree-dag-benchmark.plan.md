---
name: Quad-tree DAG benchmark
overview: Add a new `TreeFanOutBenchmark` that exercises the scheduler with a quad-tree DAG (fan-out = 4) instead of wide phase barriers, measuring how well cores stay saturated when there are always independent subtrees to run.
todos:
  - id: create-benchmark
    content: Create TreeFanOutBenchmark.cs with quad-tree DAG wiring, reusing ComputeJob
    status: pending
  - id: run-verify
    content: Build, run benchmark, verify results look reasonable and compare to theoretical optimal
    status: pending
isProject: false
---

# Quad-Tree DAG Benchmark

## Design

A complete quad-tree (fan-out = 4) where every internal node is a `ComputeJob` that, upon completion, readies its 4 children. No global barriers — just local parent-child dependencies.

- **Depth 6** = 1 root + 4 + 16 + 64 + 256 + 1024 + 4096 = **5,461 total jobs** (close enough to the ~6K range)
- **Depth 7** would give ~21K jobs — probably too many. Depth 6 is a good starting point.
- At any point in execution, there are up to `4^level` independent runnable jobs at the frontier, so by depth 3 there are 64 runnable jobs — more than enough for 12 cores.
- Each job does the same `ComputeIterations = 12,500` hash-mixing work as the existing benchmark, reusing `ComputeJob`.

### DAG shape

```
Root (depth 0)
├── Child 0 (depth 1)
│   ├── Child 0.0 (depth 2)
│   │   ├── ... (4 children each)
│   │   └── ...
│   └── Child 0.3
├── Child 1
├── Child 2
└── Child 3
```

### Wiring

Pre-allocate a flat array of `ComputeJob[5461]`. For node at index `i`, its 4 children are at indices `4*i + 1` through `4*i + 4`. Each child calls `DependsOn(parent)`. Submit the root.

### Theoretical optimal

All 5,461 jobs x ~15-20us each = ~82-109ms of total work. Divided by 12 cores = ~6.8-9.1ms optimal. But the tree has depth 6, so the critical path is 7 sequential jobs (~105-140us). The gap between critical path and optimal will reveal how well the scheduler exploits the available parallelism.

## Files to change

- **New file**: `benchmarks/Fabrica.SampleGame.Benchmarks/Scale/TreeFanOutBenchmark.cs` — the benchmark class, following the same patterns as [RealisticTickBenchmark.cs](benchmarks/Fabrica.SampleGame.Benchmarks/Scale/RealisticTickBenchmark.cs) (same DiagnoserConfig, same warmup/analysis structure).
- Reuses existing `ComputeJob` from [ComputeJob.cs](benchmarks/Fabrica.SampleGame.Benchmarks/Scale/ComputeJob.cs) — no changes needed there.
- No changes to engine code.

## Benchmark structure

- `GlobalSetup`: allocate `ComputeJob[5461]`, create pool + scheduler, run 10 warmup ticks
- `OneTick`: wire the tree (each child `DependsOn` its parent, set iterations/seed), submit root, wait for completion
- `GlobalCleanup`: print tree-specific analysis (critical path depth, frontier width over time, gap to optimal)
- Include the same `DiagnoserConfig` (Memory, Threading, Median, P95, Op/s) as the existing benchmark
