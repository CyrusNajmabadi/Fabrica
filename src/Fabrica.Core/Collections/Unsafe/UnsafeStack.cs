using System.Runtime.CompilerServices;

namespace Fabrica.Core.Collections.Unsafe;

/// <summary>
/// Minimal LIFO stack backed by an <see cref="UnsafeList{T}"/>. Provides push/pop semantics
/// with the same unchecked-access performance characteristics.
///
/// WARNING: This is a readonly struct wrapping a mutable reference-type backing store.
/// Copies of this struct share the same underlying <see cref="UnsafeList{T}"/>, so mutations
/// through one copy are visible through all others. Do not copy instances — always pass by
/// reference or store in a single location. Accidental copies will silently alias state.
/// </summary>
internal readonly struct UnsafeStack<T>(int initialCapacity)
{
    private readonly UnsafeList<T> _list = new(initialCapacity);

    public static UnsafeStack<T> Create() => new(initialCapacity: 16);

    public int Count
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
