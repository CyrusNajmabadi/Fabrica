# ArenaCoordinator Benchmark Results

**Machine:** Apple M4 Max, .NET 10, Release, ShortRunJob  
**Date:** 2026-04-03

## Coordinator Pipeline (1M-node tree, depth 19)

Each benchmark performs N=1000 operations on a ~1M-node persistent binary tree.
Divide Mean by N for per-operation cost.

| Method                      | Mean (µs) | Per-op (µs) | StdDev (µs) | Allocated |
|-----------------------------|----------:|------------:|------------:|----------:|
| BufferFill_Only             |       313 |        0.31 |          17 |    344 KB |
| Coordinator_MergeOnly       |     1,663 |        1.66 |         114 |    536 KB |
| Coordinator_ForkThenRelease |     2,622 |        2.62 |         335 |    439 KB |

### Interpretation

- **Buffer fill**: ~310 ns to create 20 spine nodes in a `ThreadLocalBuffer`. This is the worker-side cost.
- **Merge (no release)**: ~1.66 µs for the full merge pipeline: allocate 20 global slots, copy 20 nodes, fixup
  tagged local references, increment 40 child refcounts. This is 4.7x the raw fork cost (~356 ns) from
  `SingleForkReleaseBenchmarks`, reflecting the overhead of index remapping and the two-phase copy+fixup.
- **Full cycle (merge + release)**: ~2.62 µs per change. Compared to raw fork+release (~1.1 µs), the coordinator
  adds ~1.5 µs per operation — the cost of the buffer, fixup pass, and release collection.

### Allocation Note

The allocations shown include creating a **new** `ThreadLocalBuffer` per iteration. In production, buffers are
pooled and reused via `Clear()`, eliminating steady-state allocation. The residual allocation comes from
BenchmarkDotNet array params and the `new Random(42)` seed.

### Budget Estimate

At 2.62 µs per change, a 1ms budget allows ~380 tree changes per tick. For a simulation running at 40 Hz
(25 ms/tick), dedicating 1ms to tree operations supports 380 version-creating changes per frame.

## Unsafe Optimization Pass

Added `Unsafe.Add` + `MemoryMarshal.GetArrayDataReference` to `ThreadLocalBuffer` append and indexer
(matching the pattern in `UnsafeStack` and `UnsafeSlabDirectory`). Debug builds still use standard array access.

| Method                      | Before (µs) | After (µs) | Improvement |
|-----------------------------|------------:|------------|-------------|
| BufferFill_Only             |         313 |        268 |        ~14% |
| Coordinator_MergeOnly       |       1,663 |      1,607 |         ~3% |
| Coordinator_ForkThenRelease |       2,622 |      2,477 |         ~6% |

Buffer fill shows the clearest win (bounds-check removal on append). Merge and full cycle improvements are
modest because the bottleneck at this scale is **cache pressure** from the 1M-node arena (~64 MB across many
slabs), not bounds checking. Each fixup/increment touches random slab locations, incurring L2/L3 misses.

## Comparison with Raw Arena+RefCount Benchmarks

| Operation              | Raw (µs) | Coordinator (µs) | Overhead |
|------------------------|---------:|------------------:|---------:|
| Fork only              |    0.356 |              1.61 |     4.5x |
| Release only           |    0.999 |              0.87 |     0.9x |
| Fork + Release         |    1.100 |              2.48 |     2.3x |

The release cost is essentially unchanged (cascade-free runs identically). The fork overhead is from the
indirection of buffer → coordinator → arena (vs. direct arena writes in the raw benchmark).
