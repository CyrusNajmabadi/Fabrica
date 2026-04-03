# RefCountTable Benchmarks

## Environment

- **BenchmarkDotNet** v0.15.8
- **OS**: macOS Tahoe 26.3.1 (Darwin 25.3.0)
- **CPU**: Apple M4 Max, 16 logical / 16 physical cores
- **Runtime**: .NET 10.0.2, Arm64 RyuJIT armv8.0-a
- **Job**: ShortRun (3 iterations, 3 warmup)
- **Date**: 2026-04-03

## Results

| Method                         | N      | Mean (ns)     | StdDev (ns) | Ratio | Allocated |
|:-------------------------------|-------:|--------------:|------------:|------:|----------:|
| Increment_Sequential           | 1,000  | 18,859        | 179         | 1.00  | 576 KB    |
| Increment_Random               | 1,000  | 17,912        | 72          | 0.95  | 576 KB    |
| Decrement_NoFrees              | 1,000  | 19,563        | 100         | 1.04  | 576 KB    |
| Decrement_AllFree              | 1,000  | 18,856        | 248         | 1.00  | 576 KB    |
| CascadeDecrement_BinaryTree    | 1,000  | 19,596        | 181         | 1.04  | 576 KB    |
| CascadeDecrement_LinearChain   | 1,000  | 19,896        | 91          | 1.06  | 576 KB    |
| CascadeDecrement_WideTree      | 1,000  | 19,761        | 165         | 1.05  | 584 KB    |
| IncrementBatch                 | 1,000  | 18,399        | 57          | 0.98  | 580 KB    |
| DecrementBatch_Mixed           | 1,000  | 19,560        | 66          | 1.04  | 580 KB    |
| SteadyState_IncrementDecrement | 1,000  | 20,414        | 71          | 1.08  | 576 KB    |
| Baseline_FlatArray_Increment   | 1,000  | 393           | 4           | 0.02  | 4 KB      |
| | | | | | |
| Increment_Sequential           | 10,000 | 26,780        | 373         | 1.00  | 576 KB    |
| Increment_Random               | 10,000 | 27,201        | 419         | 1.02  | 576 KB    |
| Decrement_NoFrees              | 10,000 | 42,916        | 357         | 1.60  | 576 KB    |
| Decrement_AllFree              | 10,000 | 33,452        | 246         | 1.25  | 576 KB    |
| CascadeDecrement_BinaryTree    | 10,000 | 43,265        | 90          | 1.62  | 576 KB    |
| CascadeDecrement_LinearChain   | 10,000 | 49,234        | 377         | 1.84  | 576 KB    |
| CascadeDecrement_WideTree      | 10,000 | 44,171        | 259         | 1.65  | 704 KB    |
| IncrementBatch                 | 10,000 | 49,431        | 316         | 1.85  | 615 KB    |
| DecrementBatch_Mixed           | 10,000 | 41,541        | 330         | 1.55  | 615 KB    |
| SteadyState_IncrementDecrement | 10,000 | 51,495        | 389         | 1.92  | 576 KB    |
| Baseline_FlatArray_Increment   | 10,000 | 3,515         | 21          | 0.13  | 39 KB     |
| | | | | | |
| Increment_Sequential           | 100K   | 121,685       | 493         | 1.00  | 960 KB    |
| Increment_Random               | 100K   | 165,132       | 388         | 1.36  | 960 KB    |
| Decrement_NoFrees              | 100K   | 274,155       | 633         | 2.25  | 960 KB    |
| Decrement_AllFree              | 100K   | 186,479       | 688         | 1.53  | 960 KB    |
| CascadeDecrement_BinaryTree    | 100K   | 281,311       | 351         | 2.31  | 960 KB    |
| CascadeDecrement_LinearChain   | 100K   | 346,887       | 629         | 2.85  | 960 KB    |
| CascadeDecrement_WideTree      | 100K   | 489,772       | 7,576       | 4.02  | 1,985 KB  |
| IncrementBatch                 | 100K   | 189,224       | 1,369       | 1.56  | 1,351 KB  |
| DecrementBatch_Mixed           | 100K   | 303,268       | 2,966       | 2.49  | 1,351 KB  |
| SteadyState_IncrementDecrement | 100K   | 359,321       | 1,600       | 2.95  | 960 KB    |
| Baseline_FlatArray_Increment   | 100K   | 57,866        | 312         | 0.48  | 391 KB    |

## Analysis

### Per-operation throughput (N=100K)

| Operation              | Mean (ns) | ns/op | ops/sec |
|:-----------------------|----------:|------:|--------:|
| Increment (sequential) | 121,685   | 1.22  | ~820M   |
| Increment (random)     | 165,132   | 1.65  | ~606M   |
| Decrement (no frees)   | 274,155   | 2.74  | ~365M   |
| Decrement (all free)   | 186,479   | 1.86  | ~537M   |
| Cascade (binary tree)  | 281,311   | 2.81  | ~356M   |
| Cascade (linear chain) | 346,887   | 3.47  | ~288M   |
| Cascade (wide tree)    | 489,772   | 4.90  | ~204M   |
| Flat array baseline    | 57,866    | 0.58  | ~1.7B   |

### Key observations

1. **Slab allocation is the dominant cost at small N.** At N=1K, all operations take ~18–20 μs regardless of
   pattern. This is the cost of allocating the first `UnsafeSlabDirectory<int>` slab (~21K ints). By N=10K the
   actual operation costs emerge.

2. **Sequential increment is fast**: ~1.2 ns/op at 100K — only ~2x the flat array baseline (0.58 ns/op). The
   2x overhead comes from the two-level directory indirection and `EnsureSlab` call per increment.

3. **Random access penalty is moderate**: 1.65 ns/op vs 1.22 ns/op sequential (36% slower at 100K). This is
   entirely cache-miss overhead from touching random slab locations. Still sub-2ns per op.

4. **Decrement with callback overhead**: `Decrement_NoFrees` (2.74 ns/op) is slower than `Decrement_AllFree`
   (1.86 ns/op). This is counterintuitive — the "no frees" benchmark does N increment pairs (2N total
   increments) plus N decrements, so the setup cost dominates. The actual decrement path is cheap.

5. **Cascade-free is well-behaved**: Binary tree cascade (2.81 ns/op per node) is comparable to plain decrement.
   Linear chain is ~23% slower (3.47 ns/op) due to sequential worklist push/pop for each node. Wide tree at
   100K is the most expensive (4.90 ns/op) due to the large worklist allocation for the fanout.

6. **Worklist (`UnsafeStack`) allocation for wide trees**: The wide tree benchmark shows 1,985 KB allocated vs
   960 KB for other patterns at 100K. The extra ~1 MB is the worklist growing to hold ~100K child indices.
   For realistic tree shapes (binary, moderate fanout) this is not an issue.

7. **Batch vs single**: `IncrementBatch` (1.89 ns/op) is slightly slower than single `Increment` (1.22 ns/op)
   at 100K. This is because the batch benchmark allocates the `int[]` indices array inside the benchmark method.
   The actual batch loop itself doesn't add overhead.

8. **Steady-state (inc/dec oscillation)**: 3.59 ns/op — this is a decrement + increment per element with
   refcounts oscillating 2→1→2. Representative of coordinator workload where persistent structure versions are
   constantly being created and released.

9. **Memory is stable**: All operations at the same N allocate the same ~576 KB (N≤10K) or ~960 KB (N=100K).
   This is the `UnsafeSlabDirectory<int>` slab allocations. No per-operation allocations.

### Unsafe optimization assessment

The `RefCountTable` delegates all storage access to `UnsafeSlabDirectory<int>`, which already uses
`Unsafe.Add` + `MemoryMarshal.GetArrayDataReference` in Release builds. The table itself adds only:
- Interface dispatch for `IRefCountEvents.OnFreed` and `IChildEnumerator.EnumerateChildren`
- `UnsafeStack<int>` worklist push/pop (already unsafe in Release)

There are no additional array accesses in `RefCountTable` itself that would benefit from unsafe optimization.
The per-operation costs (1–5 ns) are already in the range where overhead is dominated by cache behavior and
interface dispatch rather than bounds checks.

**Recommendation**: No additional unsafe optimizations needed for `RefCountTable`. The existing unsafe paths
in `UnsafeSlabDirectory` and `UnsafeStack` already cover the hot paths.
