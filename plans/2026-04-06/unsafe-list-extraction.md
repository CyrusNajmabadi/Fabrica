# UnsafeList Extraction

## Motivation

The growable array + count pattern appears in `UnsafeStack<T>` and will be needed by
`ThreadLocalBuffer<T>` (Phase 2). Extract a reusable `UnsafeList<T>` type, then refactor
`UnsafeStack<T>` to delegate to it.

## UnsafeList API

New file: `src/Fabrica.Core/Memory/UnsafeList.cs`

```csharp
internal sealed class UnsafeList<T>(int initialCapacity = 16)
{
    public int Count { get; }
    public ref T this[int index] { get; }        // unchecked in Release, bounds-checked in Debug
    public void Add(T item);                      // append, grow if needed
    public void RemoveLast();                     // decrement count (debug assert non-empty)
    public ReadOnlySpan<T> WrittenSpan { get; }   // _array.AsSpan(0, _count)
    public Span<T> WrittenSpanMutable { get; }    // mutable version for in-place mutation
    public void Reset();                          // set _count = 0, keep array
}
```

Same unchecked-access pattern as `UnsafeStack<T>`: release builds use
`Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(...), index)`, debug builds use normal
array indexing.

## UnsafeStack Refactor

`UnsafeStack<T>` becomes a thin wrapper around `UnsafeList<T>`:

```csharp
internal sealed class UnsafeStack<T>(int initialCapacity = 16)
{
    private readonly UnsafeList<T> _list = new(initialCapacity);

    public int Count => _list.Count;
    public void Push(T item) => _list.Add(item);
    public bool TryPop(out T item) { ... }
}
```

All existing `UnsafeStack<T>` tests pass unchanged.

## Tests

New file: `tests/Fabrica.Core.Tests/Memory/UnsafeListTests.cs`

- Empty count, Add increments, indexer returns by-ref, WrittenSpan, Reset, growth, RemoveLast,
  reference types.

## Files

| File | Change |
|------|--------|
| `src/Fabrica.Core/Memory/UnsafeList.cs` | New |
| `src/Fabrica.Core/Memory/UnsafeStack.cs` | Refactor to delegate |
| `tests/Fabrica.Core.Tests/Memory/UnsafeListTests.cs` | New |
