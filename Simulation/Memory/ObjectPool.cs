using System.Diagnostics;

namespace Simulation.Memory;

/// <summary>
/// Single-threaded, pre-allocated object pool backed by a flat array used as a stack.
/// All Rent/Return calls must originate from the same thread.
/// Zero allocation after construction — never falls back to new T() on Rent.
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
