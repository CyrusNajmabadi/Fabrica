using System.Runtime.CompilerServices;
using Fabrica.Core.Collections.Unsafe;

namespace Fabrica.Core.Threading.Queues;

/// <summary>
/// Thread-safe global injection queue used as the overflow target for <see cref="BoundedLocalQueue{T}"/>.
/// Backed by a <see cref="Lock"/> and an <see cref="UnsafeStack{T}"/> (LIFO).
///
/// LIFO ordering is intentional: recently overflowed items are likely cache-hot, so draining
/// them first improves locality. This matches Tokio's injection queue behavior.
///
/// Contention is expected to be low — this queue is only touched when a worker's local ring
/// buffer overflows (rare) or when a worker drains global work (periodic). A simple lock is
/// cheaper and simpler than a lock-free structure for this access pattern.
/// </summary>
internal readonly struct InjectionQueue<T>() where T : class
{
    private readonly Lock _lock = new();
    private readonly UnsafeStack<T> _stack = new();

    /// <summary>Injects an item into the global queue. Called from the overflow path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enqueue(T item)
    {
        using (_lock.EnterScope())
            _stack.Push(item);
    }

    /// <summary>Tries to dequeue an item. Returns <c>null</c> if the queue is empty.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T? TryDequeue()
    {
        using (_lock.EnterScope())
            return _stack.TryPop(out var item) ? item : null;
    }

    /// <summary>Approximate item count. Acquires the lock for an exact snapshot.</summary>
    public int Count
    {
        get
        {
            using (_lock.EnterScope())
                return _stack.Count;
        }
    }

    /// <summary>Approximate emptiness check.</summary>
    public bool IsEmpty
    {
        get
        {
            using (_lock.EnterScope())
                return _stack.Count == 0;
        }
    }

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor(InjectionQueue<T> queue)
    {
        /// <summary>
        /// Drains all items into a list (LIFO order — most recently enqueued first).
        /// </summary>
        public List<T> DrainToList()
        {
            var result = new List<T>();
            using (queue._lock.EnterScope())
            {
                while (queue._stack.TryPop(out var item))
                    result.Add(item);
            }

            return result;
        }
    }
}
