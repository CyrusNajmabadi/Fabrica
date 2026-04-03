# Persistent Tree Benchmarks

**Date:** 2026-04-03
**Machine:** Apple M4 Max, .NET 10, Release build
**Tree:** depth-19 complete binary tree (~1M nodes, 8-byte TreeNode structs)

## Single Fork / Release Cost (isolated)

Measured over 1,000 iterations on a depth-19 (~1M node) tree. Each fork creates a 20-node spine
(20 allocs + 40 refcount increments). Each release cascade-frees 20 old spine nodes.

| Operation       | Total (1K ops) | Per-op  | Notes                                       |
|-----------------|---------------:|--------:|---------------------------------------------|
| **Fork only**   |      356 µs    | ~356 ns | 20 allocs + 40 rc increments + tree walk     |
| **Release only**|      999 µs    | ~999 ns | 20 cascade frees + 40 rc decrements          |
| **Fork+Release**|    1,133 µs    | ~1.1 µs | Full version change (fork then release)      |

**Key insight:** Fork is ~3x cheaper than release. The fork walks down the tree (cache-sequential),
allocates from the free list, and increments refcounts. The release triggers a cascade through the
worklist, calling `OnFreed` for each spine node, which reads each node's children, decrements their
refcounts, and frees the node — more pointer-chasing and branching.

Combined fork+release (~1.1 µs) is slightly cheaper than the sum of parts (~1.35 µs) due to cache
effects: the fork warms the spine into L1/L2, making the subsequent release's reads faster.

## Raw Allocation/Release (UnsafeSlabArena + RefCountTable)

| Method                         | N       | Mean      | StdDev    | Ratio | Allocated | Alloc Ratio |
|------------------------------- |--------:|----------:|----------:|------:|----------:|------------:|
| ArenaOnly_AllocThenFree        | 100K    |  1.110 ms | 0.010 ms  |  0.58 |   7.63 MB |        0.89 |
| ArenaAndRefCount_AllocThenFree | 100K    |  1.644 ms | 0.045 ms  |  0.86 |   8.57 MB |        1.00 |
| ArenaAndRefCount_SteadyState   | 100K    |  1.916 ms | 0.035 ms  |  1.00 |   8.57 MB |        1.00 |
|                                |         |           |           |       |           |             |
| ArenaOnly_AllocThenFree        | 1M      |  7.186 ms | 0.100 ms  |  0.48 |  69.59 MB |        0.94 |
| ArenaAndRefCount_AllocThenFree | 1M      | 12.315 ms | 0.371 ms  |  0.82 |  73.96 MB |        1.00 |
| ArenaAndRefCount_SteadyState   | 1M      | 14.940 ms | 0.040 ms  |  1.00 |  73.96 MB |        1.00 |

**Key takeaways:**
- 1M arena allocations + frees: **~7.2ms** (arena-only), **~12.3ms** (arena + refcount).
  That's **~7.2ns/alloc+free** for the arena and **~12.3ns** for the combined system.
- RefCountTable adds ~70% overhead over arena-alone for the alloc+free path.
- Steady-state (alloc 1M, free 1M, alloc 1M again from free-list, free 1M again): ~15ms for 2M alloc+free cycles at 1M scale.
- The ~70MB allocation is the slab infrastructure (one-time; no per-operation GC pressure).

## Persistent Tree: Producer/Consumer Simulation

| Method                      | Changes | Mean      | StdDev    | Ratio | Allocated   |
|---------------------------- |--------:|----------:|----------:|------:|------------:|
| Interleaved_RandomLeaf      |   1,000 |  1.281 ms | 0.043 ms  |  1.00 |    64.47 KB |
| Burst_AllChangesThenRelease |   1,000 |  1.474 ms | 0.112 ms  |  1.15 |   448.50 KB |
| Windowed_ProducerAheadBy100 |   1,000 |  1.454 ms | 0.138 ms  |  1.14 |    96.38 KB |
|                             |         |           |           |       |             |
| Interleaved_RandomLeaf      |  10,000 |  9.527 ms | 0.265 ms  |  1.00 |    64.47 KB |
| Burst_AllChangesThenRelease |  10,000 | 11.161 ms | 1.099 ms  |  1.17 |  3,649.09 KB |
| Windowed_ProducerAheadBy100 |  10,000 | 10.046 ms | 0.521 ms  |  1.06 |    96.38 KB |
|                             |         |           |           |       |             |
| Interleaved_RandomLeaf      |  50,000 | 43.131 ms | 0.387 ms  |  1.00 |    64.47 KB |
| Burst_AllChangesThenRelease |  50,000 | 48.756 ms | 0.274 ms  |  1.13 | 16,067.43 KB |
| Windowed_ProducerAheadBy100 |  50,000 | 46.429 ms | 1.920 ms  |  1.08 |    96.38 KB |

**Key takeaways:**

### Per-change cost
- **~0.86 µs/change** (interleaved) for a depth-19 tree (~1M nodes).
- Each change = path-copy (20 allocs + 40 refcount increments) + release (20 frees via cascade).
- That's ~120 individual arena+refcount operations per change at **~7.2ns/op** average.

### Pattern comparison
- **Interleaved** (lock-step consumer): fastest, ~64KB allocation (just the tree setup slabs).
  Zero GC pressure during the change loop — freed nodes are immediately recycled.
- **Burst** (all changes then release): 13-15% slower. Memory grows proportional to `ChangeCount × 20`
  (new spine nodes accumulate before release). ~16MB extra allocation at 50K changes.
- **Windowed** (producer ahead by 100): only 6-8% slower than interleaved, with minimal extra memory
  (~96KB vs ~64KB). Best model for a real producer/consumer with slight lag.

### Simulation scale
- 50,000 changes (representing ~25 seconds of simulation at 2000 changes/sec) complete in **43ms**.
  The infrastructure overhead is negligible — the system can sustain **>1 million changes/second**
  on a persistent 1M-node tree.
- At 60fps with one change per frame: 60 changes in ~0.052ms. Essentially free.
- At 1000Hz simulation rate: 1000 changes in ~0.86ms. Comfortably within budget.

### Memory behaviour
- Interleaved: perfectly flat memory — free-list recycling keeps allocation constant.
- Burst: linear growth during production phase, then flat after release.
- The ~64KB base allocation is the slab infrastructure (directory + first few slabs).
