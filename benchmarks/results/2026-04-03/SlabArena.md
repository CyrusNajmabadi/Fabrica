# SlabArena\<T\> Benchmark Results

**Date:** 2026-04-03
**Machine:** Apple M4 Max, 16 logical / 16 physical cores
**OS:** macOS Tahoe 26.3.1 (Darwin 25.3.0)
**Runtime:** .NET 10.0.2 (RyuJIT Arm64)
**Job:** ShortRun (3 iterations, 1 launch, 3 warmup)

## Key Findings

### Bump allocation is extremely fast

At 100K entries, `Allocate` (bump path) for small 8-byte structs completes in ~102µs — roughly **1ns per allocation**. Medium 64-byte structs are ~6.4µs per 1K entries. The cost is dominated by slab array allocation, not the index arithmetic.

### Free-list reuse (FastStack with unsafe access)

Using `FastStack<int>` with `Unsafe.Add` to bypass bounds checks in Release, free-list allocation is ~7.6x the
bump path at 100K (down from ~10.5x with `Stack<int>`). Random-order free lists add ~60% on top. The unsafe
optimisation also eliminated bounds checks on the directory and slab indexer, improving all mixed workloads.

### Indexed read is fast and cache-friendly

Sequential reads at 100K entries: ~80µs (0.8ns/entry). Random reads: ~99µs (0.99ns/entry). The ~24% penalty
for random access reflects cross-slab cache misses. Both are zero-allocation.

### Steady-state patterns

- **Interleaved** (alloc one, free one): ~4.3ns/op at 100K (was ~6.8ns before unsafe opt — **36% faster**).
- **Bulk alloc then bulk free** (10 batches): ~3.9µs/1K entries (**22% faster** than before).

### Unsafe optimisation impact (N=100,000)

| Benchmark | Stack\<int\> | FastStack+Unsafe | Improvement |
|---|---|---|---|
| AllocateAndFree_Small | 815 µs | 636 µs | **22%** |
| Allocate_FromFreeList | 1,107 µs | 770 µs | **31%** |
| Allocate_FromFreeList_RandomOrder | 1,543 µs | 1,233 µs | **20%** |
| SteadyState_Interleaved | 681 µs | 434 µs | **36%** |
| SteadyState_BulkAllocThenBulkFree | 494 µs | 386 µs | **22%** |

### Large structs pay for slab allocation

4KB structs have LOH-sized slabs (1 entry/slab), so every allocation creates a new `T[1]`. At 100K this is 45ms
and 410MB — the arena overhead is negligible, the cost is purely .NET array allocation. This is the expected worst
case and confirms that slab sizing works correctly (slabs stay under 85KB).

## Full Results

| Method                             | N      | Mean            | Ratio  | Allocated   |
|----------------------------------- |------- |----------------:|-------:|------------:|
| **Allocate_Small**                 | 1000   |     17,281 ns   |   1.00 |    590 KB   |
| Allocate_Medium                    | 1000   |     17,859 ns   |   1.03 |    590 KB   |
| Allocate_Large                     | 1000   |    364,191 ns   |  21.08 |  4,655 KB   |
| AllocateAndFree_Small              | 1000   |     18,928 ns   |   1.10 |    598 KB   |
| Allocate_FromFreeList              | 1000   |     20,547 ns   |   1.19 |    598 KB   |
| Allocate_FromFreeList_RandomOrder  | 1000   |     23,362 ns   |   1.35 |    603 KB   |
| Read_Sequential                    | 1000   |        638 ns   |   0.04 |      0 B    |
| Read_Random                        | 1000   |        599 ns   |   0.03 |      0 B    |
| SteadyState_Interleaved            | 1000   |     18,624 ns   |   1.08 |    590 KB   |
| SteadyState_BulkAllocThenBulkFree  | 1000   |     20,587 ns   |   1.19 |    592 KB   |
| Baseline_FlatArray                 | 1000   |      1,877 ns   |   0.11 |     64 KB   |
| Baseline_FlatArray_Read_Sequential | 1000   |      2,272 ns   |   0.13 |     64 KB   |
|                                    |        |                 |        |             |
| **Allocate_Small**                 | 10000  |     24,431 ns   |   1.00 |    656 KB   |
| Allocate_Medium                    | 10000  |     27,989 ns   |   1.15 |  1,180 KB   |
| Allocate_Large                     | 10000  |  2,624,410 ns   | 107.45 | 41,500 KB   |
| AllocateAndFree_Small              | 10000  |     34,383 ns   |   1.41 |    787 KB   |
| Allocate_FromFreeList              | 10000  |     47,036 ns   |   1.93 |  1,311 KB   |
| Allocate_FromFreeList_RandomOrder  | 10000  |     93,470 ns   |   3.83 |  1,352 KB   |
| Read_Sequential                    | 10000  |      7,898 ns   |   0.32 |      0 B    |
| Read_Random                        | 10000  |      7,919 ns   |   0.32 |      0 B    |
| SteadyState_Interleaved            | 10000  |     40,572 ns   |   1.66 |    852 KB   |
| SteadyState_BulkAllocThenBulkFree  | 10000  |     57,501 ns   |   2.35 |    602 KB   |
| Baseline_FlatArray                 | 10000  |     51,744 ns   |   2.12 |    640 KB   |
| Baseline_FlatArray_Read_Sequential | 10000  |     57,102 ns   |   2.34 |    640 KB   |
|                                    |        |                 |        |             |
| **Allocate_Small**                 | 100000 |    101,542 ns   |   1.00 |  1,377 KB   |
| Allocate_Medium                    | 100000 |    636,018 ns   |   6.26 |  6,949 KB   |
| Allocate_Large                     | 100000 | 44,935,580 ns   | 442.53 |410,276 KB   |
| AllocateAndFree_Small              | 100000 |    635,777 ns   |   6.26 |  2,427 KB   |
| Allocate_FromFreeList              | 100000 |    769,512 ns   |   7.58 |  7,998 KB   |
| Allocate_FromFreeList_RandomOrder  | 100000 |  1,232,543 ns   |  12.14 |  8,399 KB   |
| Read_Sequential                    | 100000 |     79,518 ns   |   0.78 |      0 B    |
| Read_Random                        | 100000 |     98,581 ns   |   0.97 |      0 B    |
| SteadyState_Interleaved            | 100000 |    434,311 ns   |   4.28 |  3,737 KB   |
| SteadyState_BulkAllocThenBulkFree  | 100000 |    386,046 ns   |   3.80 |  1,351 KB   |
| Baseline_FlatArray                 | 100000 |    313,303 ns   |   3.09 |  6,400 KB   |
| Baseline_FlatArray_Read_Sequential | 100000 |    382,300 ns   |   3.76 |  6,400 KB   |

Raw BenchmarkDotNet output: [`results/`](results/)
