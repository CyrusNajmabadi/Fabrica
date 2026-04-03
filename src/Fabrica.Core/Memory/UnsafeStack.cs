using System.Runtime.CompilerServices;
#if !DEBUG
using System.Runtime.InteropServices;
#endif

namespace Fabrica.Core.Memory;

/// <summary>
/// Minimal LIFO stack backed by a growable array. In release builds, push/pop use <see cref="Unsafe"/> to bypass
/// bounds checking for maximum throughput. In debug builds, standard array access is used so the CLR performs
/// real bounds checking.
/// </summary>
internal sealed class UnsafeStack<T>(int initialCapacity = 16)
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

#if DEBUG
        array[count] = item;
#else
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), count) = item;
#endif
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
#if DEBUG
        item = _array[newCount];
#else
        item = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_array), newCount);
#endif
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
