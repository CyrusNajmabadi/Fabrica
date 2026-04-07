using System.Diagnostics;
using System.Runtime.CompilerServices;
#if !DEBUG
using System.Runtime.InteropServices;
#endif

namespace Fabrica.Core.Collections.Unsafe;

/// <summary>
/// Growable array-backed list with O(1) indexed access. In release builds, indexing uses
/// <see cref="Unsafe"/> to bypass bounds checking for maximum throughput. In debug builds,
/// standard array access is used so the CLR performs real bounds checking.
///
/// GROWTH
///   Doubles the backing array when capacity is exceeded. <see cref="Reset"/> sets the count to
///   zero without releasing the array, so steady-state usage incurs no allocation after warmup.
///
/// THREAD MODEL
///   Single-threaded. No synchronization is provided.
/// </summary>
public sealed class UnsafeList<T>(int initialCapacity = 16)
{
    private T[] _array = new T[initialCapacity];
    private int _count;

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            Debug.Assert((uint)index < (uint)_count, $"Index {index} is out of range [0, {_count}).");
#if DEBUG
            return ref _array[index];
#else
            return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_array), index);
#endif
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(T item)
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
    public void RemoveLast()
    {
        Debug.Assert(_count > 0, "RemoveLast called on empty list.");
        _count--;
    }

    public ReadOnlySpan<T> WrittenSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _array.AsSpan(0, _count);
    }

    public Span<T> WrittenSpanMutable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _array.AsSpan(0, _count);
    }

    /// <summary>
    /// Resets the count to zero without releasing the backing array. The next work phase reuses
    /// the already-allocated capacity, achieving steady-state zero allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset() => _count = 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T[] Grow()
    {
        var newArray = new T[Math.Max(_array.Length * 2, 4)];
        Array.Copy(_array, newArray, _array.Length);
        _array = newArray;
        return newArray;
    }
}
