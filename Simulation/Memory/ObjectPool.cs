using System.Diagnostics;

namespace Simulation.Memory;

/// <summary>
/// Single-threaded, pre-allocated object pool backed by a flat array used as a stack.
/// All Rent/Return calls must come from the same thread (simulation thread).
/// Zero allocation after construction — never falls back to new T().
/// </summary>
internal sealed class ObjectPool<T> where T : class, new()
{
    private readonly T[] _items;
    private int _count;

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
        if (_count == 0) return null;
        return _items[--_count];
    }

    /// <summary>Returns an instance to the pool.</summary>
    public void Return(T item)
    {
        Debug.Assert(_count < _items.Length, "Pool overflow — returning more items than capacity");
        _items[_count++] = item;
    }

    public int Available => _count;
    public int Capacity  => _items.Length;

    /// <summary>Fraction of pool currently rented out (0.0 = all free, 1.0 = all rented).</summary>
    public double Pressure => 1.0 - (double)_count / _items.Length;
}
