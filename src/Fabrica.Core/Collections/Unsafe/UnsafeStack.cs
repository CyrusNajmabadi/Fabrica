using System.Runtime.CompilerServices;

namespace Fabrica.Core.Collections.Unsafe;

/// <summary>
/// Minimal LIFO stack backed by an <see cref="UnsafeList{T}"/>. Provides push/pop semantics
/// with the same unchecked-access performance characteristics.
/// </summary>
internal sealed class UnsafeStack<T>(int initialCapacity = 16)
{
    private readonly UnsafeList<T> _list = new(initialCapacity);

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _list.Count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(T item) => _list.Add(item);

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
