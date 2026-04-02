---
name: SlabList pipeline redesign
overview: "Replace the ChainNode linked list with a SlabList — a segmented append-only queue of contiguous arrays (\"slabs\") — for lock-free SPSC communication between production and consumption threads. The SlabList exposes a minimal four-method API with explicit thread-ownership naming: ProducerAppend, ProducerCleanup, ConsumerAcquireEntries, ConsumerReleaseEntries."
todos:
  - id: phase1-entry-slab
    content: Implement PipelineEntry<TPayload>, Slab<TPayload>, SlabSizeHelper<TPayload>
    status: completed
  - id: phase1-slablist
    content: Implement SlabList<TPayload> with ProducerAppend/ProducerCleanup/ConsumerAcquireEntries/ConsumerReleaseEntries
    status: completed
  - id: phase1-range
    content: Implement SlabRange<TPayload> with indexer, enumerator, Count, IsEmpty
    status: completed
  - id: phase1-tests
    content: "Unit tests: single-threaded append/acquire/release, slab transitions, cleanup, pinning, range iteration, slab sizing"
    status: completed
  - id: phase2-loops
    content: Integrate SlabList into ProductionLoop and ConsumptionLoop, update IConsumer interface, simplify SharedPipelineState
    status: pending
  - id: phase3-engine
    content: Update SimulationEngine, RenderCoordinator, test doubles, and all test files for new IConsumer shape
    status: pending
  - id: phase4-cleanup
    content: Remove ChainNode, BaseProductionLoop chain management, node ObjectPool, SharedPipelineState if subsumed
    status: pending
isProject: false
---

# SlabList Pipeline Redesign

## Motivation

The current `ChainNode` linked list forces the consumer to chase pointers through individually-allocated heap objects. The `SlabList` replaces this with contiguous array segments ("slabs") for cache-friendly sequential access, while maintaining the same lock-free SPSC guarantees.

## Core Data Types

### `PipelineEntry<TPayload>` — per-element struct stored in slab arrays

Replaces `ChainNode` as the unit of data. Lightweight value type, no heap allocation per tick.

```csharp
public readonly struct PipelineEntry<TPayload>
{
    public required TPayload Payload { get; init; }
    public required long PublishTimeNanoseconds { get; init; }
}
```

No sequence number field — the sequence is implicit from the entry's global position in the `SlabList` (position 0 = tick 0, position 1 = tick 1, etc.).

**File**: new `src/Fabrica.Pipeline/Pipeline/PipelineEntry.cs`

### `Slab<TPayload>` — a contiguous array segment

A class (heap-allocated, poolable) that holds a fixed-size array of entries. Slabs form a forward-linked chain.

```csharp
internal sealed class Slab<TPayload>
{
    public readonly PipelineEntry<TPayload>[] Entries;
    public Slab<TPayload>? Next;
    public long LogicalStartPosition;
}
```

- `Entries` — fixed-size array, sized via `SlabSizeHelper<TPayload>`
- `Next` — forward link to the next slab (plain field — covered by the position's release fence)
- `LogicalStartPosition` — the global position that `Entries[0]` maps to; set once when the slab is created/reused

**File**: new `src/Fabrica.Pipeline/Pipeline/Slab.cs`

### `SlabSizeHelper<TPayload>` — Roslyn-inspired LOH-aware sizing

Static generic helper (computed once per `TPayload` instantiation by the JIT). Mirrors Roslyn's `SegmentedArrayHelper`.

```csharp
internal static class SlabSizeHelper<TPayload>
{
    public static readonly int SlabLength = ...;  // largest power-of-2 under LOH (~85,000 bytes)
    public static readonly int SlabShift  = ...;  // log2(SlabLength) — for bit-shift indexing
    public static readonly int OffsetMask = ...;  // SlabLength - 1 — for masking
}
```

**File**: new `src/Fabrica.Pipeline/Pipeline/SlabSizeHelper.cs`

### `SlabRange<TPayload>` — the consumer's view of acquired entries

A lightweight `ref struct` returned by `ConsumerAcquireEntries`. Represents a contiguous range of entries across one or more slabs.

```csharp
public readonly ref struct SlabRange<TPayload>
{
    public long Count { get; }
    public bool IsEmpty => Count == 0;
    public ref readonly PipelineEntry<TPayload> this[long index] { get; }
    public Enumerator GetEnumerator();
}
```

- Indexer maps global-offset to slab + local offset using bit-shift/mask
- Struct enumerator for zero-allocation `foreach`
- `ref struct` so it can return `ref readonly` entries without copies

**File**: new `src/Fabrica.Pipeline/Pipeline/SlabRange.cs`

### `SlabList<TPayload>` — the core SPSC data structure

Owns the slab chain and two volatile position cursors. Replaces `BaseProductionLoop`'s chain state and `SharedPipelineState`'s `LatestNode`/`ConsumptionEpoch`.

```csharp
public sealed class SlabList<TPayload>
{
    // ── Volatile cursors (SPSC synchronization points) ──────────────
    private long _producerPosition;   // Volatile.Write by producer, Volatile.Read by consumer
    private long _consumerPosition;   // Volatile.Write by consumer, Volatile.Read by producer

    // ── Producer-owned state ────────────────────────────────────────
    private Slab<TPayload> _headSlab;     // first slab (for reclamation walk)
    private Slab<TPayload> _tailSlab;     // last slab (for appending)
    private long _cleanupPosition;        // how far producer has cleared

    // ── Consumer-owned state ────────────────────────────────────────
    private Slab<TPayload>? _consumerSlab; // cached slab pointer for reads
}
```

#### Four-method API with explicit thread ownership

```csharp
// ── Producer thread ─────────────────────────────────────────────
void ProducerAppendEntry(in PipelineEntry<TPayload> entry);
void ProducerCleanupReleasedEntries<THandler>(ref THandler handler)
    where THandler : struct, IEntryCleanupHandler<TPayload>;

// ── Consumer thread ─────────────────────────────────────────────
SlabRange<TPayload> ConsumerAcquireEntries();
void ConsumerReleaseEntries(in SlabRange<TPayload> range);
```

**Design decisions documented in code comments:**

- **Publish-per-append**: `ProducerAppend` writes the entry and then performs the volatile write of `_producerPosition` in the same call. This makes each entry visible to the consumer immediately upon append, reducing rendering latency when the production loop processes multiple ticks in one iteration. This is safe because the SPSC release/acquire pair on `_producerPosition` ensures all entry writes are visible before the consumer reads them. (A design note will mention that batched publishing was considered but offers no correctness or meaningful performance benefit.)
- **No exposed position getters**: `_producerPosition` and `_consumerPosition` are purely internal. The consumer checks for new data via `ConsumerAcquireEntries()` returning an empty range. Diagnostics/metrics getters can be added later if needed.
- **Whole-range consumption**: The API is intentionally acquire-the-whole-range / release-the-whole-range. The consumer always processes all available entries. A design comment will note that if partial consumption were ever needed, the API could generalize to `GetRange(start, end)` + `AdvanceConsumer(count)`.

**File**: new `src/Fabrica.Pipeline/Pipeline/SlabList.cs`

## How the Loops Change (Phase 2)

### ProductionLoop

Current flow per tick in `[ProductionLoop.cs](src/Fabrica.Pipeline/Pipeline/ProductionLoop.cs)`:

1. `_producer.Produce(current.Payload)` -> new payload
2. `AppendToChain(payload, publishTime)` -> new `ChainNode` + volatile write of `LatestNode`
3. `CleanupStaleNodes(consumptionEpoch)`

New flow per tick:

1. `_producer.Produce(previousPayload)` -> new payload
2. `_slabList.ProducerAppend(new PipelineEntry { ... })` (writes entry + volatile publish)
3. `_slabList.ProducerCleanup(_pinnedVersions)` (incremental clear up to consumer position)

`ProductionLoop` will no longer inherit from `BaseProductionLoop`. It will own a `SlabList<TPayload>` directly. The class hierarchy simplifies significantly.

### ConsumptionLoop

Current flow per frame in `[ConsumptionLoop.cs](src/Fabrica.Pipeline/Pipeline/ConsumptionLoop.cs)`:

1. Volatile-read `LatestNode`, rotate previous/latest pair
2. `_consumer.Consume(previous, latest, frameStart, ct)`
3. `_shared.ConsumptionEpoch = _previous.SequenceNumber`

New flow per frame:

1. `var range = _slabList.ConsumerAcquireEntries()`
2. If `range.IsEmpty`, skip to throttle
3. `_consumer.Consume(range, frameStart, ct)`
4. Deferred consumer scheduling using entries in range
5. `_slabList.ConsumerReleaseEntries(range)`
6. Throttle

### IConsumer interface change

```csharp
public interface IConsumer<TPayload>
{
    void Consume(
        SlabRange<TPayload> entries,
        long frameStartNanoseconds,
        CancellationToken cancellationToken);
}
```

### IProducer — minimal change

`IProducer<TPayload>` stays the same. The only change is that `Produce` receives the previous payload directly (from slablist's last entry) rather than via `CurrentNode.Payload`.

### IDeferredConsumer — stays the same

Still receives a single `TPayload` and pins by position (now a `long` instead of `int`). The `DeferredConsumerScheduler` reads the latest entry from the range for dispatch.

### PinnedVersions — key type change

`Pin`/`Unpin`/`IsPinned` change from `int sequenceNumber` to `long position` to match the slab global position type.

### SharedPipelineState — simplified or removed

`SharedPipelineState` currently holds `LatestNode` and `ConsumptionEpoch`. Both are replaced by the `SlabList`'s internal volatile cursors. `PinnedVersions` is the only piece that survives and can live as a standalone field passed to both the `SlabList` and `DeferredConsumerScheduler`.

## Pinning / Cleanup Integration

During `ProducerCleanup`:

```
for position in [_cleanupPosition .. consumerPosition):
    entry = slab[position]
    if pinnedVersions.IsPinned(position):
        _pinnedQueue.Add(copy of entry)  // struct copy to side table
    else:
        ReleasePayloadResources(entry.Payload)
    slab[position] = default  // clear slot, help GC

    if position crosses slab boundary:
        return completed slab to pool

DrainUnpinnedEntries() — same pattern as today's DrainUnpinnedNodes
```

## Implementation Phases

### Phase 1: Data structures + unit tests (this PR)

Build all new types alongside old ones (no deletion). Files:

- `PipelineEntry.cs`, `Slab.cs`, `SlabSizeHelper.cs`, `SlabRange.cs`, `SlabList.cs`
- `tests/Fabrica.Pipeline.Tests/Pipeline/SlabListTests.cs`
- `tests/Fabrica.Pipeline.Tests/Pipeline/SlabRangeTests.cs`
- `tests/Fabrica.Pipeline.Tests/Pipeline/SlabSizeHelperTests.cs`

Tests cover: single-threaded append/acquire/release, slab boundary transitions, cleanup with and without pinning, range iteration + indexer, slab sizing for various payload sizes.

### Phase 2: Loop integration

- Rewrite `ProductionLoop` to use `SlabList` instead of inheriting `BaseProductionLoop`
- Rewrite `ConsumptionLoop` to use `ConsumerAcquireEntries`/`ConsumerReleaseEntries`
- Change `IConsumer<TPayload>.Consume` signature to take `SlabRange<TPayload>`
- Update `PinnedVersions` from `int` to `long`
- Simplify or remove `SharedPipelineState`

### Phase 3: Engine + test updates

- Update `SimulationEngine`'s `RenderCoordinator` (implements `IConsumer`)
- Update all test doubles (`TestConsumer`, `TestWorkerConsumer`, etc.)
- Update all test files that reference `ChainNode`, `previous`/`latest` pairs, etc.

### Phase 4: Old code removal

- Delete `BaseProductionLoop` and all its partial files (`ChainNode.cs`, `PrivateChainNode.cs`, `NodeMutation.cs`, `ChainNodeAllocator.cs`, `ChainTestAccessor.cs`)
- Delete `SharedPipelineState` (if fully subsumed)
- Remove `ChainNode`-flavored `ObjectPool` usage
- Delete `ChainNodeTests.cs`, `ChainNodePayloadTests.cs`

