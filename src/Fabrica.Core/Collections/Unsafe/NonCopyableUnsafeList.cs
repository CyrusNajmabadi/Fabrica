using System.Diagnostics;
using System.Runtime.CompilerServices;
#if UNSAFE_OPT
using System.Runtime.InteropServices;
#endif

namespace Fabrica.Core.Collections.Unsafe;

/// <summary>
/// Growable array-backed list with O(1) indexed access. When <c>UNSAFE_OPT</c> is defined, indexing uses
/// <see cref="System.Runtime.CompilerServices.Unsafe"/> to bypass bounds checking for maximum throughput.
/// Otherwise, standard array access is used so the CLR performs real bounds checking.
///
/// GROWTH
///   Doubles the backing array when capacity is exceeded. <see cref="Reset"/> sets the count to
///   zero without releasing the array, so steady-state usage incurs no allocation after warmup.
///
/// THREAD MODEL
///   Single-threaded. No synchronization is provided.
///
/// WARNING: This is a mutable struct. Copies share the same backing array but have independent
/// counts — after a copy, mutations to one are NOT visible through the other, and a Grow() on
/// one leaves the other pointing at the old (smaller) array. Never copy this struct. Always
/// store in a single location and pass by reference.
/// </summary>
internal struct NonCopyableUnsafeList<T>(int initialCapacity)
{
    private T[] _array = new T[initialCapacity];
    private int _count;

    public static NonCopyableUnsafeList<T> Create() => new(initialCapacity: 16);

    /// <summary>Wraps an existing array with count = 0, for reuse from a pool.</summary>
    internal static NonCopyableUnsafeList<T> Wrap(T[] array) => new(0) { _array = array };

    /// <summary>Returns the raw backing array. Use only in release-mode unsafe paths.</summary>
    internal readonly T[] UnsafeBackingArray
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _array;
    }

    /// <summary>Returns an immutable snapshot of the current array and count.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ReadOnlyArray<T> AsReadOnly() => new(_array, _count);

    /// <summary>True when this struct has been properly constructed (backing array is non-null).</summary>
    public readonly bool IsInitialized
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _array != null;
    }

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => _count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            Debug.Assert((uint)value <= (uint)_array.Length, $"Count {value} exceeds capacity {_array.Length}.");
            _count = value;
        }
    }

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            Debug.Assert((uint)index < (uint)_count, $"Index {index} is out of range [0, {_count}).");
#if UNSAFE_OPT
            return ref System.Runtime.CompilerServices.Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_array), index);
#else
            return ref _array[index];
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

#if UNSAFE_OPT
        System.Runtime.CompilerServices.Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), count) = item;
#else
        array[count] = item;
#endif
        _count = count + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureCapacity(int additionalCount)
    {
        var needed = _count + additionalCount;
        if (needed > _array.Length)
            this.Grow(needed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddRange(ReadOnlySpan<T> items)
    {
        var count = _count;
        var needed = count + items.Length;
        var array = _array;

        if (needed > array.Length)
            array = this.Grow(needed);

        items.CopyTo(array.AsSpan(count));
        _count = needed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemoveLast()
    {
        Debug.Assert(_count > 0, "RemoveLast called on empty list.");
        _count--;
    }

    public readonly ReadOnlySpan<T> WrittenSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _array.AsSpan(0, _count);
    }

    public readonly Span<T> WrittenSpanMutable
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
    private T[] Grow() => this.Grow(0);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T[] Grow(int minCapacity)
    {
        var newArray = new T[Math.Max(Math.Max(_array.Length * 2, minCapacity), 4)];
        Array.Copy(_array, newArray, _array.Length);
        _array = newArray;
        return newArray;
    }
}
