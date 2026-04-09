---
name: Align with Tokio patterns
overview: "Address three differences from Tokio: add atomic null-out in steal paths (GC cleanup), add Phase 2 debug assert, and remove the single-item TrySteal in favor of TryStealHalf only."
todos:
  - id: null-steal
    content: Add Interlocked.CompareExchange null-out in TrySteal and Interlocked.Exchange in TryStealHalf copy loop
    status: completed
  - id: phase2-assert
    content: Add Debug.Assert(steal != real) on Phase 2 CAS failure in TryStealHalf
    status: completed
  - id: remove-trysteal
    content: Delete TrySteal method from BoundedLocalQueue
    status: completed
  - id: update-tests
    content: Rewrite stress tests and unit tests that use TrySteal to use TryStealHalf with a destination queue
    status: completed
  - id: update-docs
    content: Update class doc comments to reflect changes
    status: completed
isProject: false
---

# Align BoundedLocalQueue with Tokio Patterns

## 1. Atomic null-out in steal paths (GC cleanup)

In [BoundedLocalQueue.cs](src/Fabrica.Core/Threading/Queues/BoundedLocalQueue.cs):

- **TrySteal**: After the speculative read + successful CAS, add a conditional null:
  ```csharp
  Interlocked.CompareExchange(ref _buffer[real & Mask], null, value);
  ```
  Only nulls if the slot still holds our reference. If the owner already overwrote it with a new item, the compare-exchange fails silently.

- **TryStealHalf copy loop**: Replace `Volatile.Read` with `Interlocked.Exchange`:
  ```csharp
  destination._buffer[dstIdx] = Interlocked.Exchange(ref _buffer[srcIdx], null);
  ```
  Safe because during the copy window (Phase 1 to Phase 2), the owner cannot write to the claimed range.

- **TryPop**: Already nulls with a plain store after CAS. No change needed (owner-only, sequential).

- **Overflow loop**: Owner-only, sequential with push. No change needed.

## 2. Phase 2 debug assert

In `TryStealHalf`, on Phase 2 CAS failure, add:
```csharp
var (actualSteal, actualReal) = Unpack(phase2Result);
Debug.Assert(actualSteal != actualReal, 
    "Phase 2 CAS failed but steal == real — invariant violated");
```
Matches Tokio's assert. Between Phase 1 and Phase 2, only the owner can change `_head` (advancing `real`). `steal` cannot move because `steal != real` blocks all other stealers.

## 3. Remove TrySteal, keep only TryStealHalf

- **Delete `TrySteal` method** from `BoundedLocalQueue.cs`.
- **Update stress tests** in [BoundedLocalQueueStressTests.cs](tests/Fabrica.Core.Tests/Collections/BoundedLocalQueueStressTests.cs): tests that use `TrySteal` should use `TryStealHalf(thiefLocalQueue, out item)` instead. Each thief thread gets its own `BoundedLocalQueue` as a destination (some tests already do this for steal-half tests).
- **Update unit tests** in [BoundedLocalQueueTests.cs](tests/Fabrica.Core.Tests/Collections/BoundedLocalQueueTests.cs): same change.
- Tokio only has batch steal. `TryStealHalf` already handles n=1 (returns one item directly, destination tail unchanged). Single-item steal is just batch steal where only one item is available.

## 4. Update doc comments

Remove references to `TrySteal` in the class-level doc. Update the BUFFER ACCESS PATTERN section to reflect the new null-out strategy.
