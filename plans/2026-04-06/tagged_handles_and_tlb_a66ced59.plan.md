---
name: Tagged handles and TLB
overview: Implement the tagged handle encoding utilities, ThreadLocalBuffer, and INodeHandleRewriter to support cross-thread references during parallel job execution and the subsequent coordinator merge phase.
todos:
  - id: tagged-handle
    content: Create TaggedHandle static utility class with encode/decode/classify methods and tests
    status: pending
  - id: thread-local-buffer
    content: Create ThreadLocalBuffer<T> append-only buffer with local handle allocation and tests
    status: pending
  - id: handle-rewriter-interface
    content: Create INodeHandleRewriter interface and add RewriteChildren to INodeChildEnumerator<T>
    status: pending
  - id: update-enumerators
    content: Add RewriteChildren to all 13 existing INodeChildEnumerator implementations
    status: pending
  - id: rewriter-tests
    content: Add tests for handle rewriting through the enumerator/rewriter pattern
    status: pending
isProject: false
---

# Phase 2: Tagged Handles, ThreadLocalBuffer, INodeHandleRewriter

## Context

During the parallel job phase, workers create new nodes in thread-local buffers. These nodes reference each other via handles that must encode *which thread's buffer* the target lives in. After the job phase, the coordinator rewrites these local handles to global arena indices. This PR adds the three building blocks for that flow.

## Components

### 2A. TaggedHandle (static utility class)

New file: `src/Fabrica.Core/Memory/TaggedHandle.cs`

`Handle<T>` stays unchanged -- it remains a clean `int` wrapper. `TaggedHandle` is a static class with encoding/decoding helpers that interpret the raw `int` contextually during work phases and merge.

Bit layout:

- **Global** (bit 31 = 0): `[0][31 bits: global arena index]` -- range 0..2B
- **Local** (bit 31 = 1): `[1][7 bits: thread ID][24 bits: local index]` -- 128 threads, 16M nodes/thread
- **None** (-1 / 0xFFFFFFFF): sentinel, neither global nor local

Key methods:

- `IsGlobal(int index)` / `IsLocal(int index)`
- `EncodeLocal(int threadId, int localIndex)` -> `int`
- `DecodeThreadId(int index)` -> `int` / `DecodeLocalIndex(int index)` -> `int`
- Constants: `MaxThreads = 128`, `MaxLocalIndex = 0x00FF_FFFF`, `LocalBit = unchecked((int)0x8000_0000u)`

All methods are `[MethodImpl(AggressiveInlining)]` static. Debug asserts validate ranges on encode.

### 2B. ThreadLocalBuffer (`src/Fabrica.Core/Memory/ThreadLocalBuffer.cs`)

Append-only buffer per worker thread per node type. Workers allocate nodes here; the coordinator drains them after the join barrier.

```csharp
internal sealed class ThreadLocalBuffer<T> where T : struct
{
    private T[] _data;
    private int _count;
    private readonly int _threadId;

    public Handle<T> Allocate();       // returns local handle with baked-in thread ID
    public ref T this[int localIndex]; // indexed access for fixup
    public ReadOnlySpan<T> WrittenSpan { get; }
    public int Count { get; }
    public void Reset();               // resets count to 0 for next phase, keeps array
}
```

Key design decisions:

- Uses `UnsafeStack`-style unchecked access in release builds for perf
- Growable array (doubles on overflow), reset between work phases (no reallocation in steady state)
- `Allocate()` calls `TaggedHandle.EncodeLocal(_threadId, _count)` to build the returned handle
- Initial capacity: 1024 (configurable via constructor)

### 2C. INodeHandleRewriter + RewriteChildren

New interface in `src/Fabrica.Core/Memory/INodeHandleRewriter.cs`:

```csharp
internal interface INodeHandleRewriter
{
    void Rewrite<TChild>(ref Handle<TChild> handle) where TChild : struct;
}
```

New method on `INodeChildEnumerator<TNode>`:

```csharp
void RewriteChildren<TRewriter>(ref TNode node, ref TRewriter rewriter)
    where TRewriter : struct, INodeHandleRewriter;
```

This takes `ref TNode` (not `in`) so the rewriter can mutate handles in-place. Same struct-generic pattern -- JIT specializes per rewriter type.

**Impact on existing enumerators**: 13 implementations across tests, benchmarks, and JIT baselines need the new method. Each follows a mechanical pattern:

```csharp
public void RewriteChildren<TRewriter>(ref TreeNode node, ref TRewriter rewriter)
    where TRewriter : struct, INodeHandleRewriter
{
    if (node.Left.IsValid) rewriter.Rewrite(ref node.Left);  // uses ref to field
    if (node.Right.IsValid) rewriter.Rewrite(ref node.Right);
}
```

Note: this requires the handle fields on test node types to have setters or be public fields (not get-only properties). The existing `TreeNode` types use `{ get; set; }` properties -- `ref` to a property return is not valid in C#. We need to either:

- Change the node handle fields to public fields in test types (preferred -- these are `struct`s and fields are the natural representation), OR
- Add a setter-based overload on the rewriter

I'll check the existing node definitions to confirm the right approach.

### 2D. Tests

New test file: `tests/Fabrica.Core.Tests/Memory/TaggedHandleTests.cs`

- Roundtrip encode/decode for all thread IDs and local indices
- Boundary values (0, max thread ID 127, max local index 0xFFFFFF)
- `IsGlobal` / `IsLocal` / `None` classification
- Assert on out-of-range encode (debug only)

New test file: `tests/Fabrica.Core.Tests/Memory/ThreadLocalBufferTests.cs`

- Allocate returns sequential local handles with correct thread ID
- `WrittenSpan` returns allocated data
- `Reset` clears count but array survives
- Growth on overflow
- Indexed access matches allocated data

Rewriter test (can go in existing `NodeStoreTests.cs` or new file):

- Rewrite local handles to global handles in a node, verify result

## Files Changed


| File                                                        | Change                       |
| ----------------------------------------------------------- | ---------------------------- |
| `src/Fabrica.Core/Memory/TaggedHandle.cs`                   | New                          |
| `src/Fabrica.Core/Memory/ThreadLocalBuffer.cs`              | New                          |
| `src/Fabrica.Core/Memory/INodeHandleRewriter.cs`            | New                          |
| `src/Fabrica.Core/Memory/INodeChildEnumerator.cs`           | Add `RewriteChildren` method |
| `tests/Fabrica.Core.Tests/Memory/TaggedHandleTests.cs`      | New                          |
| `tests/Fabrica.Core.Tests/Memory/ThreadLocalBufferTests.cs` | New                          |
| 13 existing `INodeChildEnumerator` implementations          | Add `RewriteChildren`        |


## Property access concern

The existing test node types use C# properties for handle fields:

```csharp
private struct TreeNode
{
    public Handle<TreeNode> Left { get; set; }
    public Handle<TreeNode> Right { get; set; }
}
```

`ref node.Left` does not compile for a property. The `RewriteChildren` method needs `ref` access to mutate handles in place. Options:

1. **Convert handle properties to fields** on the node structs (test types + JIT baselines). This is the natural representation for value-type structs with no invariants.
2. **Use a by-value approach**: `var h = node.Left; rewriter.Rewrite(ref h); node.Left = h;` -- works with properties but is slightly less elegant.

Option 2 is the pragmatic choice -- it avoids touching 13+ node struct definitions and doesn't require fields. The rewriter still gets a `ref` to a local, which the JIT will optimize identically.