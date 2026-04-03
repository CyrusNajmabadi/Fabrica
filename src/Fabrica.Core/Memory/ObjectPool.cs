using Fabrica.Core.Threading;

namespace Fabrica.Core.Memory;

/// <summary>
/// Single-threaded object pool backed by a stack.
///
/// DESIGN GOALS
///   Reuse over allocation: pre-allocates an initial batch of T instances in the constructor for cache warmth and to reduce early
///   GC pressure. Subsequent Rent calls return pooled instances when available, or allocate new ones when the pool is empty —
///   Rent never returns null.
///
///   Always accepts returns: Return pushes the item onto the stack regardless of the pool's current size, so objects are never
///   leaked to the GC when they could be reused. The allocator's <see cref="IAllocator{T}.Reset"/> method is called before each
///   push to guarantee pooled instances are clean.
///
///   Single-threaded: all Rent/Return calls must come from the owning thread. In production this is always the simulation thread
///   (or a dedicated SimulationWorker in the future multi-threaded model). The #if DEBUG thread-ID assertion detects accidental
///   cross-thread access early.
///
///   LIFO reuse order gives recently-returned objects the best chance of still being in CPU cache.
///
///   Zero-overhead allocation strategy: the <typeparamref name="TAllocator"/> is constrained to struct so the JIT specialises
///   every call, eliminating all interface dispatch in the hot path.
/// </summary>
public sealed class ObjectPool<T, TAllocator>
    where T : class
    where TAllocator : struct, IAllocator<T>
{
    private readonly Stack<T> _items;
    private SingleThreadedOwner _owner;

    public ObjectPool(int initialCapacity)
    {
        _items = new Stack<T>(initialCapacity);
        for (var i = 0; i < initialCapacity; i++)
            _items.Push(default(TAllocator).Allocate());
    }

    /// <summary>
    /// Returns a pooled instance if available, or allocates a new one. Never returns null.
    /// </summary>
    public T Rent()
    {
        _owner.AssertOwnerThread();
        return _items.Count > 0 ? _items.Pop() : default(TAllocator).Allocate();
    }

    /// <summary>
    /// Resets the item via <see cref="IAllocator{T}.Reset"/> and returns it to the pool for future reuse.
    /// </summary>
    public void Return(T item)
    {
        _owner.AssertOwnerThread();
        default(TAllocator).Reset(item);
        _items.Push(item);
    }

    public int Available => _items.Count;
}
