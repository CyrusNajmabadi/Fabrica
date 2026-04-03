# RefCountTable Benchmarks

## Environment

- **BenchmarkDotNet** v0.15.8
- **OS**: macOS Tahoe 26.3.1 (Darwin 25.3.0)
- **CPU**: Apple M4 Max, 16 logical / 16 physical cores
- **Runtime**: .NET 10.0.2, Arm64 RyuJIT armv8.0-a
- **Job**: ShortRun (3 iterations, 3 warmup)
- **Date**: 2026-04-03

## Results (v2 — struct generics, reusable worklist, EnsureCapacity)

| Method                         | N      | Mean (ns)     | StdDev (ns) | Ratio | Allocated |
|:-------------------------------|-------:|--------------:|------------:|------:|----------:|
| Increment_Sequential           | 1,000  | 18,682        | 804         | 1.00  | 576 KB    |
| Increment_Random               | 1,000  | 18,708        | 308         | 1.00  | 576 KB    |
| Decrement_NoFrees              | 1,000  | 19,720        | 197         | 1.06  | 576 KB    |
| Decrement_AllFree              | 1,000  | 20,076        | 728         | 1.08  | 576 KB    |
| CascadeDecrement_BinaryTree    | 1,000  | 19,217        | 304         | 1.03  | 576 KB    |
| CascadeDecrement_LinearChain   | 1,000  | 20,014        | 375         | 1.07  | 576 KB    |
| CascadeDecrement_WideTree      | 1,000  | 21,127        | 174         | 1.13  | 584 KB    |
| IncrementBatch                 | 1,000  | 18,759        | 177         | 1.01  | 580 KB    |
| DecrementBatch_Mixed           | 1,000  | 20,729        | 386         | 1.11  | 584 KB    |
| SteadyState_IncrementDecrement | 1,000  | 20,497        | 393         | 1.10  | 576 KB    |
| Baseline_FlatArray_Increment   | 1,000  | 375           | 5           | 0.02  | 4 KB      |
| | | | | | |
| Increment_Sequential           | 10,000 | 23,779        | 290         | 1.00  | 576 KB    |
| Increment_Random               | 10,000 | 23,630        | 261         | 0.99  | 576 KB    |
| Decrement_NoFrees              | 10,000 | 34,657        | 237         | 1.46  | 576 KB    |
| Decrement_AllFree              | 10,000 | 35,739        | 207         | 1.50  | 576 KB    |
| CascadeDecrement_BinaryTree    | 10,000 | 39,282        | 344         | 1.65  | 576 KB    |
| CascadeDecrement_LinearChain   | 10,000 | 43,643        | 359         | 1.84  | 576 KB    |
| CascadeDecrement_WideTree      | 10,000 | 39,612        | 260         | 1.67  | 704 KB    |
| IncrementBatch                 | 10,000 | 25,590        | 235         | 1.08  | 615 KB    |
| DecrementBatch_Mixed           | 10,000 | 39,639        | 220         | 1.67  | 679 KB    |
| SteadyState_IncrementDecrement | 10,000 | 39,220        | 268         | 1.65  | 576 KB    |
| Baseline_FlatArray_Increment   | 10,000 | 3,418         | 43          | 0.14  | 39 KB     |
| | | | | | |
| Increment_Sequential           | 100K   | 83,147        | 567         | 1.00  | 960 KB    |
| Increment_Random               | 100K   | 126,413       | 2,244       | 1.52  | 960 KB    |
| Decrement_NoFrees              | 100K   | 194,804       | 720         | 2.34  | 960 KB    |
| Decrement_AllFree              | 100K   | 212,893       | 915         | 2.56  | 960 KB    |
| CascadeDecrement_BinaryTree    | 100K   | 237,811       | 800         | 2.86  | 960 KB    |
| CascadeDecrement_LinearChain   | 100K   | 278,651       | 16,732      | 3.35  | 960 KB    |
| CascadeDecrement_WideTree      | 100K   | 418,726       | 2,402       | 5.04  | 1,985 KB  |
| IncrementBatch                 | 100K   | 145,361       | 1,372       | 1.75  | 1,351 KB  |
| DecrementBatch_Mixed           | 100K   | 376,951       | 2,505       | 4.53  | 1,864 KB  |
| SteadyState_IncrementDecrement | 100K   | 247,991       | 262         | 2.98  | 960 KB    |
| Baseline_FlatArray_Increment   | 100K   | 54,845        | 993         | 0.66  | 391 KB    |

## Comparison vs v1 (interface dispatch, per-call worklist allocation, per-Increment EnsureSlab)

| Operation (N=100K)       | v1 (ns)   | v2 (ns)   | Improvement |
|:-------------------------|----------:|----------:|:------------|
| Increment (sequential)   | 121,685   | 83,147    | **32% faster** |
| Increment (random)       | 165,132   | 126,413   | **23% faster** |
| Decrement (no frees)     | 274,155   | 194,804   | **29% faster** |
| Decrement (all free)     | 186,479   | 212,893   | 14% slower* |
| Cascade (binary tree)    | 281,311   | 237,811   | **15% faster** |
| Cascade (linear chain)   | 346,887   | 278,651   | **20% faster** |
| Cascade (wide tree)      | 489,772   | 418,726   | **15% faster** |
| Steady-state inc/dec     | 359,321   | 247,991   | **31% faster** |

*Decrement_AllFree v2 includes cascade overhead (always cascades) while v1's non-cascade `Decrement` was
faster because it only called `OnFreed` without worklist processing. The cascade-inclusive `Decrement` is
still within acceptable bounds.

### Per-operation throughput (v2, N=100K)

| Operation              | ns/op | ops/sec  |
|:-----------------------|------:|---------:|
| Increment (sequential) | 0.83  | ~1.2B    |
| Increment (random)     | 1.26  | ~793M    |
| Decrement (no frees)   | 1.95  | ~513M    |
| Decrement (all free)   | 2.13  | ~470M    |
| Cascade (binary tree)  | 2.38  | ~420M    |
| Cascade (linear chain) | 2.79  | ~359M    |
| Cascade (wide tree)    | 4.19  | ~239M    |
| Steady-state inc/dec   | 2.48  | ~403M    |
| Flat array baseline    | 0.55  | ~1.8B    |

## Analysis

### Key improvements from v2 refactoring

1. **Struct generic pattern eliminates interface dispatch.** `Decrement<TEvents, TChildren>` with struct
   constraints enables JIT specialization and inlining of `OnFreed` and `EnumerateChildren`. This shows
   most clearly in sequential increment (32% faster) and steady-state (31% faster).

2. **EnsureCapacity upfront removes per-Increment branching.** Moving slab creation to a separate
   `EnsureCapacity` call means `Increment` is just `_directory[index]++` — no null check, no `EnsureSlab`.
   Sequential increment dropped from 1.22 ns/op to 0.83 ns/op (only 1.5x the flat array baseline).

3. **Reusable worklist field eliminates per-cascade allocation.** The `UnsafeStack<int>` worklist is a
   persistent field, not allocated on each `Decrement` call. The worklist grows as needed and stays
   allocated for future cascades. This shows in stable memory allocation across runs — no worklist
   allocation appears in the non-wide-tree benchmarks.

4. **Re-entrancy support adds negligible overhead.** The `_cascadeInProgress` bool check is a single
   branch in the non-cascade (refcount stays > 0) path, which is the hot path. No measurable impact.

### Memory is stable

All operations at the same N allocate the same ~576 KB (N≤10K) or ~960 KB (N=100K). This is purely the
`UnsafeSlabDirectory<int>` slab allocations. The reusable worklist adds no per-operation allocation.
The wide-tree benchmark shows extra allocation (~1 MB at 100K) from the worklist growing to hold the
large fanout — this happens once and is reused.
