using System.Diagnostics;

namespace Simulation.Memory;

/// <summary>
/// Single-threaded object pool backed by a stack.
///
/// DESIGN GOALS
///   Reuse over allocation: pre-allocates an initial batch of T instances in the
///   constructor for cache warmth and to reduce early GC pressure.  Subsequent
///   Rent calls return pooled instances when available, or allocate new ones
///   when the pool is empty — Rent never returns null.
///
///   Always accepts returns: Return pushes the item onto the stack regardless
///   of the pool's current size, so objects are never leaked to the GC when
///   they could be reused.
///
///   Single-threaded: all Rent/Return calls must come from the owning thread.
///   In production this is always the simulation thread (or a dedicated
///   SimulationWorker in the future multi-threaded model).  The #if DEBUG
///   thread-ID assertion detects accidental cross-thread access early.
///
///   LIFO reuse order gives recently-returned objects the best chance of
///   still being in CPU cache.
/// </summary>
internal sealed class ObjectPool<T> where T : class, new()
{
    private readonly Stack<T> _items;

#if DEBUG
    private int _ownerThreadId = -1;

    private void AssertOwnerThread()
    {
        var current = Environment.CurrentManagedThreadId;
        if (_ownerThreadId == -1)
            _ownerThreadId = current;
        else
            Debug.Assert(
                _ownerThreadId == current,
                $"ObjectPool<{typeof(T).Name}> accessed from thread {current} " +
                $"but owner is thread {_ownerThreadId}. This pool is not thread-safe.");
    }
#endif

    public ObjectPool(int initialCapacity)
    {
        _items = new Stack<T>(initialCapacity);
        for (var i = 0; i < initialCapacity; i++)
            _items.Push(new T());
    }

    /// <summary>
    /// Returns a pooled instance if available, or allocates a new one.
    /// Never returns null.
    /// </summary>
    public T Rent()
    {
#if DEBUG
        this.AssertOwnerThread();
#endif
        return _items.Count > 0 ? _items.Pop() : new T();
    }

    /// <summary>Returns an instance to the pool for future reuse.</summary>
    public void Return(T item)
    {
#if DEBUG
        this.AssertOwnerThread();
#endif
        _items.Push(item);
    }

    public int Available => _items.Count;
}
