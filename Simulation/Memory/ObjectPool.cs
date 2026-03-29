using System.Diagnostics;

namespace Simulation.Memory;

/// <summary>
/// Single-threaded, pre-allocated object pool backed by a flat array used as a stack.
///
/// DESIGN GOALS
///   Zero allocation after construction: all T instances are created upfront in
///   the constructor.  Rent returns null rather than falling back to new T() when
///   exhausted.  This gives the caller explicit control over pool pressure — the
///   simulation loop uses a null return to trigger backpressure and cleanup rather
///   than silently over-allocating and stressing the GC.
///
///   Single-threaded: all Rent/Return calls must come from the owning thread.
///   In production this is always the simulation thread.  The #if DEBUG thread-ID
///   assertion detects accidental cross-thread access in tests early, before it
///   causes data races.
///
///   Flat array as stack: the top-of-stack index (_count) makes Rent and Return
///   O(1) with no heap allocation.  LIFO reuse order gives recently-returned
///   objects the best chance of still being in CPU cache.
/// </summary>
internal sealed class ObjectPool<T> where T : class, new()
{
    private readonly T[] _items;
    private int _count;

#if DEBUG
    private int _ownerThreadId = -1;

    private void AssertOwnerThread()
    {
        int current = Environment.CurrentManagedThreadId;
        if (_ownerThreadId == -1)
            _ownerThreadId = current;
        else
            Debug.Assert(
                _ownerThreadId == current,
                $"ObjectPool<{typeof(T).Name}> accessed from thread {current} " +
                $"but owner is thread {_ownerThreadId}. This pool is not thread-safe.");
    }
#endif

    public ObjectPool(int capacity)
    {
        _items = new T[capacity];
        for (int i = 0; i < capacity; i++)
            _items[i] = new T();
        _count = capacity;
    }

    /// <summary>Returns an instance from the pool, or null if exhausted.</summary>
    public T? Rent()
    {
#if DEBUG
        AssertOwnerThread();
#endif
        if (_count == 0) return null;
        return _items[--_count];
    }

    /// <summary>Returns an instance to the pool.</summary>
    public void Return(T item)
    {
#if DEBUG
        AssertOwnerThread();
        Debug.Assert(_count < _items.Length, "Pool overflow — more items returned than capacity allows.");
#endif
        _items[_count++] = item;
    }

    public int Available => _count;
    public int Capacity  => _items.Length;
}
