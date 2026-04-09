using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
internal struct InjectionQueue<T>() where T : class
{
    private readonly Lock _lock = new();
    private UnsafeStack<T> _stack = UnsafeStack<T>.Create();

    /// <summary>Injects an item into the global queue. Called from the overflow path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enqueue(T item)
    {
        using (_lock.EnterScope())
            _stack.Push(item);
    }

    /// <summary>
    /// Injects items from up to two contiguous ring-buffer segments plus one extra item,
    /// all under a single lock acquisition. The two segments handle circular-buffer wrap-around.
    /// Allocates space once in the backing stack and uses bulk <see cref="ReadOnlySpan{T}.CopyTo"/>
    /// instead of per-element pushes.
    /// </summary>
    public void EnqueueBatch(ReadOnlySpan<T?> segment1, ReadOnlySpan<T?> segment2, T extraItem)
    {
        using (_lock.EnterScope())
        {
            _stack.EnsureCapacity(segment1.Length + segment2.Length + 1);
            _stack.PushRange(AsNonNull(segment1));
            _stack.PushRange(AsNonNull(segment2));
            _stack.Push(extraItem);
        }
    }

    /// <summary>
    /// Zero-cost reinterpret of <c>ReadOnlySpan&lt;T?&gt;</c> as <c>ReadOnlySpan&lt;T&gt;</c>.
    /// Safe because <c>T : class</c> guarantees identical runtime representation.
    /// </summary>
    private static ReadOnlySpan<T> AsNonNull(ReadOnlySpan<T?> span)
        => MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<T?, T>(ref MemoryMarshal.GetReference(span)),
            span.Length);

    /// <summary>Tries to dequeue an item. Returns <c>null</c> if the queue is empty.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T? TryDequeue()
    {
        using (_lock.EnterScope())
            return _stack.TryPop(out var item) ? item : null;
    }

    /// <summary>Approximate item count. Acquires the lock for an exact snapshot.</summary>
    public readonly int Count
    {
        get
        {
            using (_lock.EnterScope())
                return _stack.Count;
        }
    }

    /// <summary>Approximate emptiness check.</summary>
    public readonly bool IsEmpty
    {
        get
        {
            using (_lock.EnterScope())
                return _stack.Count == 0;
        }
    }

    [UnscopedRef]
    internal TestAccessor GetTestAccessor() => new(ref this);

    internal ref struct TestAccessor
    {
        private ref InjectionQueue<T> _queue;

        internal TestAccessor(ref InjectionQueue<T> queue) => _queue = ref queue;

        /// <summary>
        /// Drains all items into a list (LIFO order — most recently enqueued first).
        /// </summary>
        public List<T> DrainToList()
        {
            var result = new List<T>();
            using (_queue._lock.EnterScope())
            {
                while (_queue._stack.TryPop(out var item))
                    result.Add(item);
            }

            return result;
        }
    }
}
