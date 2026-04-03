# RefCountTable Design Research

## Problem

The arena system needs a mechanism to track how many references point to each arena entry (node). When a
reference count drops to zero, the entry must be freed and its children's refcounts decremented recursively.
This is the "cascade-free" problem.

## Prior art: deferred reference counting

Traditional deferred reference counting (DRC) batches refcount operations to reduce synchronization overhead.
Key references:

- **Levanoni & Petrank (2001)** — "An On-the-Fly Reference-Counting Garbage Collector for Java." Introduces
  sliding views and update coalescing to reduce the cost of reference counting in concurrent settings.
  https://doi.org/10.1145/504311.504309

- **Blackburn & McKinley (2003)** — "Ulterior Reference Counting: Fast Garbage Collection without a Long Wait."
  Combines deferred RC with a nursery collector, deferring young-object RC entirely.
  https://doi.org/10.1145/949305.949336

- **Bacon et al. (2001)** — "A Unified Theory of Garbage Collection." Shows that tracing and reference counting
  are duals; deferred RC is equivalent to epoch-based tracing.
  https://doi.org/10.1145/1028976.1028982

Our approach is a simplification: all RC mutations are deferred to a single coordinator thread, so no
synchronization is needed at all. Workers buffer increment/decrement requests; the coordinator processes them
in bulk.

## Design alternatives considered

### 1. Refcount stored in the arena struct itself

Rejected. Mixing refcounts with node data:
- Dirties node cache lines during RC operations (false sharing with read-heavy node traversal)
- Requires the arena to know about RC semantics
- Makes the RC layer non-reusable

Parallel arrays (Struct-of-Arrays) keep RC data separate, enabling independent cache line access.

### 2. `Dictionary<int, int>` for refcounts

Rejected. Hash table overhead (hashing, collision resolution, resizing) is unnecessary when indices are dense
sequential integers. A slab directory provides O(1) indexed access with no hashing.

### 3. Flat `int[]` for refcounts

Appealing for simplicity but requires copying on growth. The slab directory avoids copying entirely — new slabs
are appended, existing slabs never move. This is critical for the arena's no-copy guarantee.

### 4. Recursive cascade-free

Rejected. A linked list of 10,000 nodes would produce a call stack 10,000 frames deep. Iterative worklist
bounds stack depth to O(1).

### 5. Delegate-based child enumeration

Rejected. `Action<int>` or `Func<int, IEnumerable<int>>` would allocate closures and intermediate collections.
The `IChildEnumerator` interface with `ref UnsafeStack<int>` parameter allows the implementation to push
directly onto the worklist with zero allocation.

## Cascade-free: worklist pattern

The iterative worklist pattern is well-established in garbage collectors:

- **Cheney's algorithm (1970)** uses a FIFO worklist (breadth-first) for copying collection.
  Our cascade-free uses LIFO (depth-first via `UnsafeStack`) which is more cache-friendly for tree-shaped
  structures — recently freed nodes' children are likely in nearby slab regions.

- **Mark-stack in tracing GCs** — most modern tracing collectors (V8, HotSpot G1) use an explicit mark stack
  rather than recursion to avoid stack overflow on deep object graphs.
  https://v8.dev/blog/concurrent-marking

## Performance characteristics

Benchmarks on Apple M4 Max, .NET 10, Release mode (v2 — struct generics, reusable worklist):

| Operation | ns/op (N=100K) | Notes |
|:----------|---------------:|:------|
| Sequential increment | 0.83 | Best case: linear slab access, no EnsureSlab overhead |
| Random increment | 1.26 | 52% slower from cache misses |
| Cascade (binary tree) | 2.38 | Devirtualized child enumeration |
| Cascade (linear chain) | 2.79 | Sequential worklist push/pop |
| Cascade (wide tree) | 4.19 | Large worklist growth |
| Steady-state inc/dec | 2.48 | Representative coordinator workload |
| Flat array baseline | 0.55 | Lower bound (no indirection) |

The ~1.5x overhead vs flat array for sequential increment is the cost of two-level directory indirection.
Moving `EnsureSlab` out of `Increment` and using struct generic pattern for callbacks improved throughput
by 15-32% compared to the initial implementation.

## Portability notes

`RefCountTable` has no GC reliance. Storage is `int[][]` via `UnsafeSlabDirectory<int>`. In Rust this maps
to `Vec<Box<[i32]>>`. The `IRefCountEvents` and `IChildEnumerator` interfaces map to trait objects or
function pointers. The `UnsafeStack<int>` worklist maps to `Vec<i32>`.
