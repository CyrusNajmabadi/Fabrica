# Arena-Backed Persistent Data Structure with Deferred Reference Counting

## Context

Fabrica needs persistent (immutable, structurally-shared) data structures that are cache-friendly, GC-free on hot
paths, and portable to Rust/C++. Traditional heap-allocated node trees suffer from pointer chasing, GC pressure, and
cross-core cache thrashing on shared refcounts. This design replaces heap objects with value-type structs stored
contiguously in a slab-backed arena, using integer indices instead of pointers and deferred single-threaded reference
counting.

This is a **standalone reusable component** — like `ProducerConsumerQueue<T>` and `WorkStealingDeque<T>` — not tied to
any specific engine subsystem. Lives in `Fabrica.Core.Memory`.

## Architecture

```
SlabArena<T>              — slab-backed storage + O(1) lookup + free list
RefCountTable             — parallel int array, single-threaded inc/dec/cascade
ThreadLocalBuffer<T>      — per-thread append buffer with local indices
ArenaCoordinator<T,TNode> — merge + fixup + refcount processing (composes the above)
```

### Storage: `SlabArena<T>` where `T : struct`

- **Pre-allocated directory**: A flat `T[][]` array of 65,536 slab pointers, allocated once (~512KB). Never grows,
  never replaced. Eliminates all directory growth concurrency concerns.
- **Slab sizing**: Power-of-2 node count per slab, computed by `SlabSizeHelper` to stay under the LOH threshold
  (85,000 bytes). For 64-byte structs: 1,024 nodes/slab = 64KB.
- **Capacity ceiling**: 65,536 slabs x 1,024 nodes = ~67 million nodes (~4GB). More than sufficient.
- **O(1) lookup**: `directory[index >> slabShift][index & slabMask]` — two array loads, no linked list walk.
- **On-demand slab allocation**: Slabs are allocated only when needed. The coordinator is the sole writer; workers only
  read via global indices.
- **Free list**: A simple list of freed indices. New allocations pop from the free list first, fall back to bumping the
  high-water index.

### Reference Counting: `RefCountTable`

- **Parallel storage**: A separate `int[][]` with the same directory/slab structure as the arena. Same index maps to
  the refcount for that node.
- **Single-threaded mutation**: Only the coordinator reads/writes refcounts. No atomics, no false sharing.
- **Cascade freeing via worklist**: When a node hits refcount 0, its children are added to a decrement worklist (not
  recursed). This bounds per-frame cost and avoids stack overflow on deep trees. Optionally cap work per tick for
  incremental freeing.

### Per-Thread Allocation: `ThreadLocalBuffer<T>`

- Workers create new nodes in a thread-local buffer (simple append-only array, no synchronization).
- Local indices start at 0 within the buffer. Child references to other new nodes use a **tag bit** (high bit set =
  local index, clear = global index).
- After the work phase, the buffer is handed to the coordinator for merge.

### Coordinator Merge: `ArenaCoordinator<T, TNode>`

The coordinator (single-threaded, runs at fork-join boundary) processes all thread-local buffers:

1. **Allocate global indices** for each new node (from free list or high-water bump).
2. **Build local-to-global remap** for each buffer.
3. **Copy structs** from thread-local buffers into the global arena, rewriting tagged local indices to global indices
   via `TNode.FixupReferences(...)`.
4. **Process increment logs**: Per-thread buffers of "these global indices gained a new parent" — bump their refcounts.
5. **Process decrement logs**: Per-thread buffers of "these global indices were released" — decrement refcounts,
   cascade-free via worklist.
6. **Return freed indices** to the free list.

### Node Contract: `IArenaNode`

Structs stored in the arena implement a trait so the coordinator can fixup references:

```csharp
interface IArenaNode
{
    void FixupReferences(ReadOnlySpan<int> localToGlobalMap);
}
```

The struct knows which of its `int` fields are child indices. During fixup, it checks the tag bit and remaps local
indices. This is struct-constrained for JIT specialization (same pattern as `IAllocator<T>`).

### Handle Type: `ArenaHandle`

```csharp
readonly struct ArenaHandle
{
    public readonly int Index;
}
```

4 bytes. Debug builds add a generation counter for stale-reference detection. Not needed for correctness (correct
refcounting guarantees no stale references), but valuable as a safety net.

## Key Properties

- **Zero atomics on the hot path**: Workers only append to thread-local buffers. Coordinator is single-threaded.
- **Cache-friendly**: Structs packed contiguously in slabs. SoA split means refcount passes don't touch node data.
- **GC-free by construction**: Integers and arrays only. Portable to Rust/C++.
- **Persistent/immutable reads are lock-free**: Workers read the shared tree via global indices into immutable data. No
  synchronization needed.
- **Steady-state zero allocation**: Free list recycling. After warmup, no new slabs allocated.
- **No cycles possible**: One-way references (children only, no parent pointers) make pure refcounting sufficient.

## Fragmentation Note

Over time, the free list may scatter across slabs. This is inherent to any free-list allocator but still better than
heap pointer-chasing. Mitigation: prefer recently-freed indices (LIFO free list = cache-hot reuse). Defragmentation is
possible but deferred as a future optimization.

## Implementation Order

### PR 1: `SlabArena<T>` — Storage Primitive

- Pre-allocated directory, on-demand slab allocation, O(1) lookup by index
- Free list (single-threaded push/pop)
- High-water bump allocation
- `SlabSizeHelper` integration for LOH-aware slab sizing
- Comprehensive tests: allocation, lookup, free list recycling, capacity

### PR 2: `RefCountTable` — Parallel Refcount Array

- Same directory/slab structure as `SlabArena<T>`
- Single-threaded increment, decrement, and cascade-free with worklist
- Tests: basic inc/dec, cascade through tree structures, worklist bounds

### PR 3+4 (Combined): `ThreadLocalBuffer<T>`, `IArenaNode`, `ArenaCoordinator<TNode>`

Combined into a single PR since the components are tightly coupled and deliver value only together.

- **`ArenaIndex`**: Static helper for tag/untag/sentinel convention (high bit = local index, -1 = no child).
- **`IArenaNode`**: Struct-constrained interface with `FixupReferences(ReadOnlySpan<int>)` and
  `IncrementChildren(RefCountTable)`, enabling JIT-specialized merge.
- **`ThreadLocalBuffer<T>`**: Append-only per-thread buffer with release log (`UnsafeStack<int>`), unsafe
  array access in release builds (matching `UnsafeStack`/`UnsafeSlabDirectory` pattern).
- **`ArenaCoordinator<TNode>`**: Merge pipeline (alloc globals → EnsureCapacity → copy+fixup+increment) with
  deferred release processing via `DecrementBatch`. Reusable internal arrays (no per-merge allocation).
- **Tests** (887 total, 237 new): ArenaIndex encoding, ThreadLocalBuffer operations, coordinator merge/fixup,
  persistent tree path-copy with full permutation testing (4! + 5! + 4! release orders, depth-4 sampled,
  interleaved add/release, multi-buffer, stress tests).
- **Benchmarks**: Full pipeline on 1M-node tree — ~2.5 µs per fork+release cycle through coordinator.
  Unsafe pass: ~6% improvement from bounds-check elimination. Bottleneck is cache pressure, not bounds checks.

## Resolved Questions

- **Tag bit convention**: High bit of int. Local index 0 → `int.MinValue`, max local → `-2`. `-1` reserved as
  sentinel. Max index space 2^31 (~2 billion) — more than enough.
- **Increment log granularity**: `IArenaNode.IncrementChildren` called per-node during merge — no separate log
  needed. The coordinator iterates all new nodes and calls IncrementChildren on each after fixup.
- **Slab deallocation**: Slabs stay allocated. Steady state means reuse via LIFO free list.
