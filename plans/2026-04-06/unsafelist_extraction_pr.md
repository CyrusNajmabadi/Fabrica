---
name: UnsafeList extraction PR
overview: Extract an UnsafeList<T> type from the growable array+count pattern used by UnsafeStack<T>, then refactor UnsafeStack<T> to delegate to it. This provides the foundation for ThreadLocalBuffer<T> in the tagged handles PR.
todos:
  - id: unsafe-list
    content: Create UnsafeList<T> with Add, indexer, WrittenSpan, Reset, growth
    status: completed
  - id: refactor-stack
    content: Refactor UnsafeStack<T> to delegate to UnsafeList<T>
    status: completed
  - id: list-tests
    content: Write UnsafeListTests
    status: completed
  - id: verify-stack-tests
    content: Verify all existing UnsafeStack tests still pass
    status: completed
isProject: false
---

# UnsafeList Extraction

## What

Create `UnsafeList<T>` -- a growable, unchecked-access array-backed list. Then refactor `UnsafeStack<T>` to delegate to it, eliminating the duplicated array+count+grow pattern.

## UnsafeList API

New file: `[src/Fabrica.Core/Memory/UnsafeList.cs](src/Fabrica.Core/Memory/UnsafeList.cs)`

```csharp
internal sealed class UnsafeList<T>(int initialCapacity = 16)
{
    private T[] _array = new T[initialCapacity];
    private int _count;

    public int Count { get; }

    public ref T this[int index] { get; }        // unchecked in Release, bounds-checked in Debug
    public void Add(T item);                      // append, grow if needed
    public void RemoveLast();                     // decrement count (debug assert non-empty)
    public ReadOnlySpan<T> WrittenSpan { get; }   // _array.AsSpan(0, _count)
    public Span<T> WrittenSpanMutable { get; }    // mutable version for in-place mutation
    public void Reset();                          // set _count = 0, keep array
}
```

Same unchecked-access pattern as `UnsafeStack<T>`: release builds use `Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(...), index)`, debug builds use normal array indexing.

## UnsafeStack refactor

`[src/Fabrica.Core/Memory/UnsafeStack.cs](src/Fabrica.Core/Memory/UnsafeStack.cs)` becomes a thin wrapper:

```csharp
internal sealed class UnsafeStack<T>(int initialCapacity = 16)
{
    private readonly UnsafeList<T> _list = new(initialCapacity);

    public int Count => _list.Count;

    public void Push(T item) => _list.Add(item);

    public bool TryPop(out T item)
    {
        if (_list.Count == 0) { item = default!; return false; }
        item = _list[_list.Count - 1];
        _list.RemoveLast();
        return true;
    }
}
```

All existing `UnsafeStack<T>` tests pass unchanged -- they validate behavior, not internals.

## Tests

New file: `[tests/Fabrica.Core.Tests/Memory/UnsafeListTests.cs](tests/Fabrica.Core.Tests/Memory/UnsafeListTests.cs)`

- `Empty_CountIsZero`
- `Add_IncrementsCount`
- `Indexer_ReturnsAddedItems`
- `Indexer_ReturnsByRef` (mutate via ref, verify)
- `WrittenSpan_ReflectsAdds`
- `Reset_ClearsCountKeepsCapacity`
- `GrowsBeyondInitialCapacity`
- `GrowPreservesExistingItems`
- `RemoveLast_DecrementsCount`
- `WorksWithReferenceTypes`

## Files

- `src/Fabrica.Core/Memory/UnsafeList.cs` -- new
- `src/Fabrica.Core/Memory/UnsafeStack.cs` -- refactor to delegate to UnsafeList
- `tests/Fabrica.Core.Tests/Memory/UnsafeListTests.cs` -- new
- Existing `UnsafeStackTests.cs` -- unchanged (validates behavior still works)

