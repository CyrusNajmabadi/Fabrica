---
name: SlabList pipeline redesign
overview: Replace the ChainNode linked list with a SlabList — a segmented append-only queue of contiguous arrays ("slabs") — for lock-free SPSC communication between production and consumption threads, with cache-friendly sequential access and Roslyn-inspired slab sizing to avoid the LOH.
todos:
  - id: phase1-entry-slab
    content: Implement PipelineEntry<TPayload>, Slab<TPayload>, SlabSizeHelper<TPayload>
    status: pending
  - id: phase1-slablist
    content: Implement SlabList<TPayload> with Append/Publish/GetRange/AdvanceConsumer/Cleanup
    status: pending
  - id: phase1-range
    content: Implement SlabRange<TPayload> with indexer, enumerator, Count
    status: pending
  - id: phase1-tests
    content: "Unit tests for SlabList: single-threaded append/read, slab transitions, cleanup, pinning, range iteration"
    status: pending
  - id: phase2-loops
    content: Integrate SlabList into ProductionLoop and ConsumptionLoop, update IConsumer interface
    status: pending
  - id: phase3-engine
    content: Update SimulationEngine, test doubles, and all test files for new IConsumer shape
    status: pending
  - id: phase4-cleanup
    content: Remove ChainNode, BaseProductionLoop chain management, node ObjectPool
    status: pending
isProject: false
---

# SlabList Pipeline Redesign

## Core Data Types

### `PipelineEntry<TPayload>` — the per-element struct stored in slab arrays

Replaces `ChainNode` as the unit of data. Lightweight value type, no heap allocation per tick.

```csharp
public readonly struct PipelineEntry<TPayload>
{
    public required TPayload Payload { get; init; }
    public required long PublishTimeNanoseconds { get; init; }
}
```

No sequence number field — the sequence is the entry's global position in the SlabList (position 0 = tick 0, position 1 = tick 1, etc.). The SlabList provides the mapping from global position to slab + offset.

### `Slab<TPayload>` — a contiguous array segment

A class (heap-allocated, poolable) that holds a fixed-size array of `PipelineEntry<TPayload>`. Slabs form a forward-linked chain.

```csharp
internal sealed class Slab<TPayload>
{
    public PipelineEntry<TPayload>[] Entries { get; }
    public Slab<TPayload>? Next;
    public int LogicalStartPosition;
}
```

- `Entries` — fixed-size array, sized via `SlabSizeHelper<TPayload>` (see below)
- `Next` — forward link to the next slab (plain field, not volatile — covered by the position's release fence)
- `LogicalStartPosition` — the global position that `Entries[0]` corresponds to; set once when the slab is created/reused

### `SlabSizeHelper<TPayload>` — Roslyn-inspired LOH-aware sizing

Static generic helper (computed once per TPayload instantiation by the JIT). Mirrors [Roslyn's SegmentedArrayHelper](https://github.com/dotnet/roslyn/blob/main/src/Dependencies/Collections/Segmented/SegmentedArrayHelper.cs).

```csharp
internal static class SlabSizeHelper<TPayload>
{
    // Largest power-of-2 element count whose array stays under the LOH threshold (~85,000 bytes)
    public static readonly int SlabLength = ...;
    public static readonly int SlabShift  = ...;  // log2(SlabLength) — for bit-shift indexing
    public static readonly int OffsetMask = ...;  // SlabLength - 1 — for masking
}
```

### `SlabRange<TPayload>` — the consumer's view

A lightweight struct returned to the consumer. Represents a contiguous range of entries across one or more slabs. Replaces what `(previous, latest)` ChainNodes represented.

```csharp
public readonly ref struct SlabRange<TPayload>
{
    // points at the slab containing the start position, plus the offset within it
    // count = end - start
    
    public int Count { get; }
    public ref readonly PipelineEntry<TPayload> this[int index] { get; }
    public Enumerator GetEnumerator();
}
```

- Indexer: `index 0` reads from the start slab at the start offset; crossing a slab boundary follows `slab.Next` (rare, amortized O(1))
- Struct enumerator for zero-allocation `foreach`
- `ref struct` so it can return `ref readonly` entries without copies

### `SlabList<TPayload>` — the core data structure

Owns the linked chain of slabs and the two volatile position cursors. This replaces `BaseProductionLoop`'s chain state and `SharedPipelineState`'s `LatestNode`/`ConsumptionEpoch`.

```csharp
public sealed class SlabList<TPayload>
{
    // ── Volatile cursors (the SPSC synchronization points) ──────────
    private long _producerPosition;   // Volatile.Write by producer, Volatile.Read by consumer
    private long _consumerPosition;   // Volatile.Write by consumer, Volatile.Read by producer

    // ── Producer-owned state ────────────────────────────────────────
    private Slab<TPayload> _headSlab;     // first slab in the chain (for reclamation)
    private Slab<TPayload> _tailSlab;     // last slab in the chain (for appending)
    private long _cleanupPosition;        // how far producer has cleared

    // ── API ─────────────────────────────────────────────────────────
    // Producer thread:
    public void Append(in PipelineEntry<TPayload> entry);
    public long ProducerPosition { get; }            // current write position (volatile read)
    public void PublishPosition();                    // Volatile.Write of _producerPosition
    
    // Consumer thread:
    public long PublishedPosition { get; }            // Volatile.Read of _producerPosition
    public SlabRange<TPayload> GetRange(long start, long end);
    public void AdvanceConsumer(long position);       // Volatile.Write of _consumerPosition

    // Producer thread (cleanup):
    public long ConsumedPosition { get; }             // Volatile.Read of _consumerPosition
    public void Cleanup(long throughPosition, PinnedVersions pins, ...);
}
```

Key point about `Append` + `PublishPosition`: the producer may call `Append` multiple times in one tick (e.g., if it processes multiple ticks in one iteration), then calls `PublishPosition` once at the end. The volatile write happens only on publish, not on every append. This mirrors the current design where `LatestNode` is set once after the tick completes.

## How the Loops Change

### ProductionLoop

Current flow per tick:

1. `_producer.Produce(current.Payload)` → new payload
2. `AppendToChain(payload, publishTime)` → new ChainNode
3. `_shared.LatestNode = node` (volatile write)
4. `CleanupStaleNodes(consumptionEpoch)`

New flow per tick:

1. `_producer.Produce(previousPayload)` → new payload
2. `_slabList.Append(new PipelineEntry { Payload = payload, PublishTimeNanoseconds = now })`
3. `_slabList.PublishPosition()` (volatile write)
4. `_slabList.Cleanup(consumedPosition, _pinnedVersions, ...)` — incremental clear + pinning check

### ConsumptionLoop

Current flow per frame:

1. `latestNode = _shared.LatestNode` (volatile read)
2. Rotate previous/latest pair
3. `_consumer.Consume(previous, latest, frameStart, ct)`
4. `_shared.ConsumptionEpoch = _previous.SequenceNumber` (volatile write)

New flow per frame:

1. `publishedPosition = _slabList.PublishedPosition` (volatile read)
2. If changed: `range = _slabList.GetRange(_lastConsumedPosition, publishedPosition)`
3. `_consumer.Consume(range, frameStart, ct)`
4. `_slabList.AdvanceConsumer(publishedPosition)` (volatile write)

### IConsumer / IProducer interfaces

```csharp
public interface IConsumer<TPayload>
{
    void Consume(
        SlabRange<TPayload> entries,
        long frameStartNanoseconds,
        CancellationToken cancellationToken);
}

public interface IProducer<TPayload>
{
    TPayload CreateInitialPayload(CancellationToken cancellationToken);
    TPayload Produce(TPayload current, CancellationToken cancellationToken);
    void ReleaseResources(TPayload payload);
}
```

`IProducer` stays largely the same. `IConsumer` changes from receiving two ChainNodes to receiving a `SlabRange`.

### IDeferredConsumer

Stays the same — receives a single `TPayload`, pins a sequence number. During incremental cleanup, pinned entries are copied to the side table. On unpin, `ReleasePayloadResources` is called and the entry is removed.

## Pinning / Cleanup Integration

The producer maintains `_pinnedQueue` (a dictionary or list of `PinnedEntry<TPayload>` structs). During `Cleanup`:

```
for position in [_cleanupPosition .. consumedPosition):
    entry = slab[position]
    if pinnedVersions.IsPinned(position):
        _pinnedQueue.Add(new PinnedEntry { Position = position, Entry = entry })
    else:
        ReleasePayloadResources(entry.Payload)
    slab[position] = default   // clear slot
    
    if position crosses slab boundary:
        return old slab to pool
```

Then `DrainUnpinnedEntries()` checks `_pinnedQueue` for entries that have been unpinned, calls `ReleasePayloadResources`, and removes them — same as today's `DrainUnpinnedNodes`.

## Implementation Approach

This is a large refactor touching the core pipeline. Recommended approach: build the new types alongside the old ones (no deletion yet), get them tested independently, then swap in.

- **Phase 1**: `PipelineEntry`, `Slab`, `SlabSizeHelper`, `SlabRange`, `SlabList` — pure data structure with unit tests
- **Phase 2**: Integrate into `ProductionLoop` / `ConsumptionLoop`, update `IConsumer` interface
- **Phase 3**: Update `SimulationEngine` and all test doubles / test files
- **Phase 4**: Remove old `ChainNode`, `BaseProductionLoop` chain management, `ObjectPool` for nodes

