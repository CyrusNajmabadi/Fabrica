using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Fabrica.Core.Memory;

/// <summary>
/// Minimal LIFO stack backed by a growable array. In release builds, push/pop use <see cref="Unsafe"/> to bypass
/// bounds checking for maximum throughput. In debug builds, full bounds assertions are performed.
/// </summary>
internal sealed class FastStack<T>(int initialCapacity = 16)
{
    private T[] _array = new T[initialCapacity];
    private int _count;

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(T item)
    {
        var count = _count;
        var array = _array;

        if (count == array.Length)
            array = this.Grow();

        Debug.Assert((uint)count < (uint)array.Length, $"Push index {count} out of bounds [0, {array.Length}).");
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), count) = item;
        _count = count + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPop(out T item)
    {
        var count = _count;
        if (count == 0)
        {
            item = default!;
            return false;
        }

        var newCount = count - 1;
        Debug.Assert((uint)newCount < (uint)_array.Length, $"Pop index {newCount} out of bounds [0, {_array.Length}).");
        item = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_array), newCount);
        _count = newCount;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T[] Grow()
    {
        var newArray = new T[_array.Length * 2];
        Array.Copy(_array, newArray, _array.Length);
        _array = newArray;
        return newArray;
    }
}
