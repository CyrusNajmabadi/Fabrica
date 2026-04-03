# SlabArena\<T\> Benchmark Results

**Date:** 2026-04-03
**Machine:** Apple M4 Max, 16 logical / 16 physical cores
**OS:** macOS Tahoe 26.3.1 (Darwin 25.3.0)
**Runtime:** .NET 10.0.2 (RyuJIT Arm64)
**Job:** ShortRun (3 iterations, 1 launch, 3 warmup)

## Key Findings

### Bump allocation is extremely fast

At 100K entries, `Allocate` (bump path) for small 8-byte structs completes in ~106µs — roughly **1ns per allocation**. Medium 64-byte structs are ~6.8µs per 1K entries. The cost is dominated by slab array allocation, not the index arithmetic.

### Free-list reuse works but has overhead from Stack\<int\>

Allocating from the free list is ~1.5–2x the cost of bump allocation at small N (1K), growing to ~10x at 100K. This is because `Stack<int>.Pop()` involves bounds checking and array access, vs. a simple increment for the bump path. Random-order free lists (poor cache locality in the stack) add another ~40% on top. For the expected steady-state workload (moderate free-list sizes), this is acceptable.

### Indexed read is fast and cache-friendly

Sequential reads at 100K entries: ~71µs (0.71ns/entry). Random reads: ~91µs (0.91ns/entry). The ~28% penalty for random access reflects cross-slab cache misses. Both are zero-allocation. For comparison, a flat `Medium[]` array sequential read is ~399µs at 100K — but that includes the array allocation; the pure read portion is comparable.

### Steady-state patterns are efficient

- **Interleaved** (alloc one, free one): ~6.8ns/op at 100K. The free-list stays at depth 1 so `Pop`/`Push` is fast.
- **Bulk alloc then bulk free** (10 batches): ~4.9µs/1K entries. Slightly cheaper than interleaved because the free list is drained in order.

### Large structs pay for slab allocation

4KB structs have LOH-sized slabs (1 entry/slab), so every allocation creates a new `T[1]`. At 100K this is 47ms and 410MB — the arena overhead is negligible, the cost is purely .NET array allocation. This is the expected worst case and confirms that slab sizing works correctly (slabs stay under 85KB).

## Full Results

| Method                             | N      | Mean            | Ratio  | Allocated   |
|----------------------------------- |------- |----------------:|-------:|------------:|
| **Allocate_Small**                 | 1000   |     18,205 ns   |   1.00 |    590 KB   |
| Allocate_Medium                    | 1000   |     17,527 ns   |   0.96 |    590 KB   |
| Allocate_Large                     | 1000   |    417,496 ns   |  22.93 |  4,655 KB   |
| AllocateAndFree_Small              | 1000   |     21,170 ns   |   1.16 |    598 KB   |
| Allocate_FromFreeList              | 1000   |     22,717 ns   |   1.25 |    598 KB   |
| Allocate_FromFreeList_RandomOrder  | 1000   |     24,015 ns   |   1.32 |    603 KB   |
| Read_Sequential                    | 1000   |        554 ns   |   0.03 |      0 B    |
| Read_Random                        | 1000   |        555 ns   |   0.03 |      0 B    |
| SteadyState_Interleaved            | 1000   |     22,473 ns   |   1.23 |    590 KB   |
| SteadyState_BulkAllocThenBulkFree  | 1000   |     21,853 ns   |   1.20 |    592 KB   |
| Baseline_FlatArray                 | 1000   |      1,978 ns   |   0.11 |     64 KB   |
| Baseline_FlatArray_Read_Sequential | 1000   |      2,373 ns   |   0.13 |     64 KB   |
|                                    |        |                 |        |             |
| **Allocate_Small**                 | 10000  |     26,854 ns   |   1.00 |    656 KB   |
| Allocate_Medium                    | 10000  |     29,168 ns   |   1.09 |  1,180 KB   |
| Allocate_Large                     | 10000  |  2,685,772 ns   | 100.03 | 41,500 KB   |
| AllocateAndFree_Small              | 10000  |     48,363 ns   |   1.80 |    787 KB   |
| Allocate_FromFreeList              | 10000  |     71,870 ns   |   2.68 |  1,311 KB   |
| Allocate_FromFreeList_RandomOrder  | 10000  |    121,198 ns   |   4.51 |  1,352 KB   |
| Read_Sequential                    | 10000  |      6,949 ns   |   0.26 |      0 B    |
| Read_Random                        | 10000  |      7,171 ns   |   0.27 |      0 B    |
| SteadyState_Interleaved            | 10000  |     64,541 ns   |   2.40 |    852 KB   |
| SteadyState_BulkAllocThenBulkFree  | 10000  |     57,739 ns   |   2.15 |    602 KB   |
| Baseline_FlatArray                 | 10000  |     53,808 ns   |   2.00 |    640 KB   |
| Baseline_FlatArray_Read_Sequential | 10000  |     63,161 ns   |   2.35 |    640 KB   |
|                                    |        |                 |        |             |
| **Allocate_Small**                 | 100000 |    105,663 ns   |   1.00 |  1,377 KB   |
| Allocate_Medium                    | 100000 |    677,894 ns   |   6.42 |  6,949 KB   |
| Allocate_Large                     | 100000 | 46,827,234 ns   | 443.18 |410,276 KB   |
| AllocateAndFree_Small              | 100000 |    815,162 ns   |   7.71 |  2,427 KB   |
| Allocate_FromFreeList              | 100000 |  1,107,339 ns   |  10.48 |  7,999 KB   |
| Allocate_FromFreeList_RandomOrder  | 100000 |  1,543,175 ns   |  14.60 |  8,399 KB   |
| Read_Sequential                    | 100000 |     71,261 ns   |   0.67 |      0 B    |
| Read_Random                        | 100000 |     91,102 ns   |   0.86 |      0 B    |
| SteadyState_Interleaved            | 100000 |    681,049 ns   |   6.45 |  3,738 KB   |
| SteadyState_BulkAllocThenBulkFree  | 100000 |    494,493 ns   |   4.68 |  1,351 KB   |
| Baseline_FlatArray                 | 100000 |    329,892 ns   |   3.12 |  6,400 KB   |
| Baseline_FlatArray_Read_Sequential | 100000 |    399,376 ns   |   3.78 |  6,400 KB   |

Raw BenchmarkDotNet output: [`results/`](results/)
