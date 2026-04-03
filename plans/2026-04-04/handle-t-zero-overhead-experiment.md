# Handle<T> Zero-Overhead Experiment

## Goal

Prove that wrapping `int` indices in a `readonly struct Handle<T>` and making `RefCountTable` generic on `T` has zero performance overhead, before committing to a codebase-wide migration.

## New Files (temporary, experiment only)

### 1. `src/Fabrica.Core/Memory/Handle.cs`

Minimal definition:

```csharp
internal readonly struct Handle<T>(int index) where T : struct
{
    public static readonly Handle<T> None = new(-1);
    public int Index { get; } = index;
    public bool IsValid => Index >= 0;
}
```

### 2. `src/Fabrica.Core/Memory/RefCountTableGeneric.cs`

A copy of `RefCountTable` renamed to `RefCountTable<T> where T : struct`. Changes from the original:

- Class: `RefCountTable<T> where T : struct`
- All public `int index` parameters become `Handle<T> handle`, internal access via `handle.Index`
- `IRefCountHandler.OnFreed(Handle<T> handle, RefCountTable<T> table)`
- `IncrementBatch(ReadOnlySpan<Handle<T>>)`, `DecrementBatch<THandler>(ReadOnlySpan<Handle<T>>, ...)`
- Cascade pending stack: `UnsafeStack<Handle<T>>` instead of `UnsafeStack<int>`
- `EnsureCapacity(int)` stays `int`
- Internal `UnsafeSlabDirectory<int>` storage unchanged

### 3. `benchmarks/Fabrica.Core.Benchmarks/RefCountTableGenericBenchmarks.cs`

A copy of `RefCountTableBenchmarks` targeting `RefCountTable<T>`. Changes:

- Uses a dummy `struct DummyNode { }` as the type parameter
- Handler structs implement `RefCountTable<DummyNode>.IRefCountHandler`
- All `int` index arithmetic stays the same but wrapped in `new Handle<DummyNode>(...)` at API boundaries
- Same N values (1K, 10K, 100K), same benchmark methods, same patterns

## Run Protocol

1. Run the **existing** `RefCountTableBenchmarks` to get a fresh baseline
2. Run the **new** `RefCountTableGenericBenchmarks` on the same machine in the same session
3. Compare mean/stddev for each method+N pair — looking for any regression above noise (~2-3%)

## Decision Point

- **Zero overhead confirmed**: Proceed with the full `Handle<T>` plan (replace `RefCountTable` with `RefCountTable<T>`, update all consumers)
- **Measurable regression**: Investigate JIT output, consider alternatives (e.g., keep `RefCountTable` non-generic, bridge in `NodeStore`)

## Cleanup

If we proceed with the full plan, `RefCountTableGeneric.cs` and the benchmark copy are deleted — the real `RefCountTable` becomes `RefCountTable<T>` and the existing benchmarks are updated in place.

## Outcome

**Zero overhead confirmed.** All benchmarks within noise (0-3%) at N=100K. The JIT erases the `Handle<T>` wrapper and generic type parameter completely. See `benchmarks/results/2026-04-04/handle-t-experiment/comparison.md` for full results.

Merged as PR #110. Proceeded with the full migration (PR #111).
