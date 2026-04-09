using System.Runtime.CompilerServices;

namespace Fabrica.Core.Collections.Unsafe;

/// <summary>
/// Minimal LIFO stack backed by an <see cref="UnsafeList{T}"/>. Provides push/pop semantics
/// with the same unchecked-access performance characteristics.
///
/// WARNING: This is a mutable struct. Copies share the same backing array but have independent
/// counts — mutations to one copy are NOT visible through the other. Never copy this struct.
/// Always store in a single location and pass by reference.
/// </summary>
internal struct UnsafeStack<T>(int initialCapacity)
{
    private UnsafeList<T> _list = new(initialCapacity);

    public static UnsafeStack<T> Create() => new(initialCapacity: 16);

    public readonly int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _list.Count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureCapacity(int additionalCount) => _list.EnsureCapacity(additionalCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(T item) => _list.Add(item);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushRange(ReadOnlySpan<T> items) => _list.AddRange(items);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPop(out T item)
    {
        var count = _list.Count;
        if (count == 0)
        {
            item = default!;
            return false;
        }

        item = _list[count - 1];
        _list.RemoveLast();
        return true;
    }
}
