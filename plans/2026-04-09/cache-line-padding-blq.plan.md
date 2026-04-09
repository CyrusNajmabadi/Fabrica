---
name: Cache-line padding BLQ
overview: Convert BoundedLocalQueue from class to struct with embedded cache-line padding, eliminating one pointer indirection per hot-path call and isolating _head on its own 128-byte cache line to eliminate false sharing.
todos:
  - id: struct-convert
    content: "Convert BoundedLocalQueue<T> to struct: embed CacheLinePaddedHead for _head/_tail padding, change TryStealHalf to take ref, remove readonly from WorkerContext.Deque"
    status: completed
  - id: callers
    content: "Update all callers: WorkerPool (ref for TryStealHalf destination), JobScheduler, test files"
    status: completed
  - id: test-accessor
    content: Rework TestAccessor for struct BLQ (ref struct or static helpers)
    status: completed
  - id: layout-test
    content: Add runtime test verifying _head/_tail are 128+ bytes apart using Unsafe.ByteOffset
    status: completed
  - id: build-test
    content: Build and run all tests (Debug + Release)
    status: completed
  - id: benchmark
    content: Run RealisticTickBenchmark and compare to baseline
    status: pending
  - id: pr
    content: Commit and create PR
    status: completed
isProject: false
---

# Struct BLQ with Cache-Line Padding

## Problem

1. `BoundedLocalQueue<T>` is a heap-allocated class, adding one pointer indirection on every hot-path access (`context.Deque._head` = two pointer chases).
2. All contended fields (`_head`, `_tail`, `_lifoSlot`) sit within 24 bytes — the same 128-byte cache line (Apple Silicon). Every thief CAS on `_head` invalidates the owner's cached `_tail`/`_lifoSlot`.

## Approach

Convert BLQ from `class` to `struct`, embedded inline in `WorkerContext` (a class). For cache-line padding, embed a **non-generic explicit-layout struct** for `_head`/`_tail` (since `LayoutKind.Explicit` is not allowed on generic types).

## Target Layout (within WorkerContext heap object)

```
BLQ struct field      Access pattern             Cache line
-----------------     ----------------------     ----------
_ht.Head (long)       owner CAS + thief CAS      Line 0 (isolated)
[120 bytes padding]
_ht.Tail (int)        owner write, thief read     Line 1
_lifoSlot (T?)        owner Xchg, thief Xchg      Line 1
_buffer (T?[])        owner + thief read           Line 1+
_overflow             owner only (rare)            Line 1+
_owner                owner only (debug)           Line 1+
```

## Implementation

### 1. Add embedded padding struct + convert BLQ to struct

In `BoundedLocalQueue.cs`:

```csharp
[StructLayout(LayoutKind.Explicit)]
internal struct CacheLinePaddedHead
{
    [FieldOffset(0)]   internal long Head;
    [FieldOffset(128)] internal int Tail;
}

internal struct BoundedLocalQueue<T> where T : class
{
    private CacheLinePaddedHead _ht;
    private T? _lifoSlot;
    private readonly T?[] _buffer;
    private readonly InjectionQueue<T> _overflow;
    private SingleThreadedOwner _owner;

    public BoundedLocalQueue(InjectionQueue<T> overflow)
    {
        _buffer = new T?[QueueCapacity];
        _overflow = overflow;
    }
    // All _head accesses become _ht.Head, all _tail accesses become _ht.Tail
    ...
}
```

Structs default to `LayoutKind.Sequential`, so `_ht` comes first (132+ bytes), then `_lifoSlot`, `_buffer`, etc. — all in declaration order.

### 2. Update TryStealHalf signature

```csharp
public T? TryStealHalf(ref BoundedLocalQueue<T> destination)
```

All callers pass `ref`. This is the key safety mechanism — prevents accidental value-copy of the 200+ byte struct.

### 3. Update WorkerContext

```csharp
internal BoundedLocalQueue<Job> Deque;  // no readonly — struct mutations must be in-place
```

Initialize in the primary constructor or inline initializer.

### 4. Update WorkerPool call sites

- `target.Deque.TryStealHalf(ref context.Deque)` — adds `ref`
- `context.Deque.Push(...)`, `context.Deque.TryPop()` — no change needed (non-readonly struct field, mutates in-place)

### 5. Rework TestAccessor

TestAccessor currently captures `BoundedLocalQueue<T> queue` by value. Options:

- Make it a `ref struct` with `ref BoundedLocalQueue<T>` (C# 11+) — cleanest
- Or use static helper methods

### 6. Update tests

- All `TryStealHalf(thief)` calls become `TryStealHalf(ref thief)`
- TestAccessor construction may change depending on approach chosen

### 7. Runtime layout verification test

```csharp
var queue = new BoundedLocalQueue<string>(overflow);
var headOffset = Unsafe.ByteOffset(ref Unsafe.As<long, byte>(ref queue._ht.Head), ...);
// Assert head and tail are >= 128 bytes apart
```

### 8. Benchmark

Run `RealisticTickBenchmark` — expected wins from reduced indirection and reduced cache-line bouncing.