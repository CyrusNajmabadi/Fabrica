# RefCountTable — Arena PR 2

## Context

This is PR 2 in the arena-backed persistent data structure system (see `arena-persistent-structures.md`).
PR 1 (`UnsafeSlabArena<T>`) provides the indexed storage. This PR adds the parallel refcount layer.

## Design decisions

### Storage: reuse `UnsafeSlabDirectory<int>`

Rather than inventing a new storage mechanism, `RefCountTable` reuses `UnsafeSlabDirectory<int>` — the same
two-level slab structure with O(1) bit-shift indexing that `UnsafeSlabArena<T>` uses. This means:
- Same slab sizing via `SlabSizeHelper<int>` (ints are 4 bytes → ~21K entries per slab under the LOH threshold)
- Same on-demand slab allocation
- Same pre-allocated directory (65K slots → supports billions of entries)
- Same Debug/Release unsafe access paths — no new unsafe code needed

### Separation of concerns: table does NOT own the free list

When a refcount hits zero, `RefCountTable` calls `IRefCountEvents.OnFreed(index)` rather than managing a
free list internally. This keeps the table independent of `UnsafeSlabArena` and lets the coordinator (future
PR 4) decide what to do with freed indices — typically returning them to the arena's free list.

### Cascade-free: iterative worklist, not recursion

Releasing a root node of a deep tree could cascade through thousands of children. Recursion risks stack
overflow. Instead, `DecrementCascade` uses an `UnsafeStack<int>` worklist:

1. Decrement the target index
2. If it hits zero, push onto worklist
3. Loop: pop from worklist, call `OnFreed`, enumerate children, decrement each child, push any that hit zero
4. Continue until worklist is empty

This bounds stack depth to O(1) regardless of tree depth. The worklist grows on the heap as needed.

### Child enumeration via interface, not delegate

`IChildEnumerator.EnumerateChildren(index, ref worklist, table)` receives the worklist and table by ref so it
can call `table.DecrementChild(childIndex, ref worklist)` directly. This avoids:
- Delegate allocation
- Closure captures
- An intermediate `List<int>` of child indices

The caller's `IChildEnumerator` implementation knows the node layout (binary tree, linked list, etc.) and
directly decrements each child inline.

### Single-threaded with debug assertions

Same pattern as `UnsafeSlabArena`: the first mutating call records the thread ID, and subsequent calls assert
same-thread. This enforces the "coordinator-only" invariant at development time without runtime cost in Release.

### Bulk operations for coordinator log processing

`IncrementBatch(ReadOnlySpan<int>)` and `DecrementBatch(ReadOnlySpan<int>, IRefCountEvents)` process
coordinator logs (from worker thread-local buffers) in a single pass. These are thin loops over the single-item
operations.

## API

```csharp
internal sealed class RefCountTable
{
    public RefCountTable();                                    // default production sizing
    internal RefCountTable(int directoryLength, int slabShift); // test injection

    public int GetCount(int index);
    public void Increment(int index);
    public void Decrement(int index, IRefCountEvents events);
    public void DecrementCascade(int index, IRefCountEvents events, IChildEnumerator children);
    public void DecrementChild(int childIndex, ref UnsafeStack<int> worklist);
    public void IncrementBatch(ReadOnlySpan<int> indices);
    public void DecrementBatch(ReadOnlySpan<int> indices, IRefCountEvents events);

    public interface IRefCountEvents { void OnFreed(int index); }
    public interface IChildEnumerator { void EnumerateChildren(int index, ref UnsafeStack<int> worklist, RefCountTable table); }
}
```

## Testing strategy

- Tiny parameters (directoryLength=4, slabShift=2 → 4 entries/slab) for edge-case coverage
- Tree shapes: binary tree (7 nodes), linear chain (100), wide tree (fanout 50), deep chain (10,000)
- Shared children: node with refcount > 1, released by two different parents
- Bulk operations: batch increment, batch decrement with mixed free/no-free
- Slab boundary: operations spanning slab 0 → slab 1
- Debug assertions: wrong-thread mutation, decrement below zero

## Benchmark strategy

11 benchmarks × 3 sizes (1K, 10K, 100K) at production-default parameters. Includes baseline flat `int[]`
increment for comparison. Results stored in `benchmarks/results/2026-04-03/RefCountTable.md`.

## Unsafe optimization assessment

No additional unsafe code needed in `RefCountTable` itself. All hot-path array accesses flow through
`UnsafeSlabDirectory<int>` (already optimized) and `UnsafeStack<int>` (already optimized). The remaining
overhead is interface dispatch for callbacks, which is inherent to the design.

## Files

- `src/Fabrica.Core/Memory/RefCountTable.cs`
- `tests/Fabrica.Core.Tests/Memory/RefCountTableTests.cs`
- `benchmarks/Fabrica.Core.Benchmarks/RefCountTableBenchmarks.cs`
- `benchmarks/results/2026-04-03/RefCountTable.md`
