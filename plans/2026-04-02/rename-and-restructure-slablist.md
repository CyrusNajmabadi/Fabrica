---
name: Rename and restructure SlabList
overview: Rename SlabList to ProducerConsumerQueue, make it generic over T (not PipelineEntry), nest Slab/Segment/ICleanupHandler inside it, and move PipelineEntry to Fabrica.Pipeline.
todos:
  - id: consolidate-types
    content: Consolidate Slab, SlabRange (Segment), SlabSizeHelper, ICleanupHandler as nested types inside ProducerConsumerQueue<T>; make generic over T instead of PipelineEntry<TPayload>
    status: completed
  - id: rename-file-methods
    content: Rename SlabList.cs to ProducerConsumerQueue.cs, rename all methods (ProducerAppend, ConsumerAcquire, ConsumerRelease, ProducerCleanup)
    status: completed
  - id: delete-old-files
    content: Delete standalone Slab.cs, SlabRange.cs, SlabSizeHelper.cs, IEntryCleanupHandler.cs
    status: completed
  - id: move-pipeline-entry
    content: Move PipelineEntry.cs to Fabrica.Pipeline/Pipeline/ with namespace Fabrica.Pipeline
    status: completed
  - id: update-tests
    content: Rename and update all three test files, remove PipelineEntry dependency from queue tests
    status: completed
  - id: verify
    content: Build, test, format
    status: completed
isProject: false
---

# Rename SlabList to ProducerConsumerQueue and restructure types

## Naming changes

- `SlabList<TPayload>` → `ProducerConsumerQueue<T>`
- `SlabRange<TPayload>` → `ProducerConsumerQueue<T>.Segment` (nested public ref struct)
- `Slab<TPayload>` → `ProducerConsumerQueue<T>.Slab` (nested private class)
- `SlabSizeHelper<TPayload>` → `ProducerConsumerQueue<T>.SlabSizeHelper` (nested private static class)
- `IEntryCleanupHandler<TPayload>` → `ProducerConsumerQueue<T>.ICleanupHandler` (nested public interface)
- Method renames: `ProducerAppendEntry` → `ProducerAppend`, `ConsumerAcquireEntries` → `ConsumerAcquire`, `ConsumerReleaseEntries` → `ConsumerRelease`, `ProducerCleanupReleasedEntries` → `ProducerCleanup`

## Generic T instead of PipelineEntry

The queue stores `T` directly (the slab array becomes `T[]`). The cleanup handler becomes `ICleanupHandler` with `HandleCleanup(long position, in T item)`. `SlabSizeHelper` computes size using `Unsafe.SizeOf<T>()` instead of `Unsafe.SizeOf<PipelineEntry<T>>()`.

## File changes

### Source files

- **Delete** `Slab.cs`, `SlabRange.cs`, `SlabSizeHelper.cs`, `IEntryCleanupHandler.cs` — all become nested types inside `[ProducerConsumerQueue.cs](src/Fabrica.Core/Collections/ProducerConsumerQueue.cs)` (rename from `SlabList.cs`)
- **Move** `[PipelineEntry.cs](src/Fabrica.Core/Collections/PipelineEntry.cs)` to `[src/Fabrica.Pipeline/Pipeline/PipelineEntry.cs](src/Fabrica.Pipeline/Pipeline/PipelineEntry.cs)`, update namespace to `Fabrica.Pipeline`
- Update doc comments throughout to reference new names

### Test files

- Rename `[SlabListTests.cs](tests/Fabrica.Core.Tests/Collections/SlabListTests.cs)` → `ProducerConsumerQueueTests.cs`, update all references
- Rename `[SlabListEdgeCaseTests.cs](tests/Fabrica.Core.Tests/Collections/SlabListEdgeCaseTests.cs)` → `ProducerConsumerQueueEdgeCaseTests.cs`, update all references
- Rename `[SlabSizeHelperTests.cs](tests/Fabrica.Core.Tests/Collections/SlabSizeHelperTests.cs)` → `ProducerConsumerQueueSlabSizeHelperTests.cs` (or similar), update to access nested `SlabSizeHelper` via the queue type
- Update `PipelineEntry` usages in tests: tests that use `PipelineEntry<string>` as the `T` type will just use `string` directly (or a simple test struct). The `SlabSizeHelperTests` that test sizing with `PipelineEntry` will need a reference to `Fabrica.Pipeline` or use a local test struct instead.

### TestAccessor

- Update `TestAccessor` to reflect new nested type names (e.g., `Slab` is now private nested, so `HeadSlab`/`TailSlab` return type changes to the nested `Slab` type which is accessible from tests via `InternalsVisibleTo`)

