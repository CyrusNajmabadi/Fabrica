using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fabrica.Core.Collections.Unsafe;

namespace Fabrica.Core.Threading.Queues;

/// <summary>
/// Thread-safe global injection queue used as the overflow target for <see cref="BoundedLocalQueue{T}"/>.
/// Backed by a <see cref="Lock"/> and an <see cref="NonCopyableUnsafeStack{T}"/> (LIFO).
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
    private NonCopyableUnsafeStack<T> _stack = NonCopyableUnsafeStack<T>.Create();
    /// <summary>
    /// Kept in sync with <see cref="_stack"/> inside the lock. Read outside the lock via
    /// <see cref="Volatile.Read(ref int)"/> to fast-reject <see cref="TryDequeue"/> when the
    /// queue is likely empty, avoiding a lock hit in the common empty case. The unsynchronized
    /// read may be momentarily stale, but never causes a missed item — only a rare unnecessary
    /// lock acquisition.
    /// </summary>
    private int _approximateCount;

    /// <summary>Injects an item into the global queue. Called from the overflow path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enqueue(T item)
    {
        lock (_lock)
        {
            _stack.Push(item);
            _approximateCount++;
        }
    }

    /// <summary>
    /// Injects items from up to two contiguous ring-buffer segments plus one extra item,
    /// all under a single lock acquisition. The two segments handle circular-buffer wrap-around.
    /// Allocates space once in the backing stack and uses bulk <see cref="ReadOnlySpan{T}.CopyTo"/>
    /// instead of per-element pushes.
    /// </summary>
    public void EnqueueBatch(ReadOnlySpan<T?> segment1, ReadOnlySpan<T?> segment2, T extraItem)
    {
        lock (_lock)
        {
            _stack.EnsureCapacity(segment1.Length + segment2.Length + 1);
            _stack.PushRange(AsNonNull(segment1));
            _stack.PushRange(AsNonNull(segment2));
            _stack.Push(extraItem);
            _approximateCount += segment1.Length + segment2.Length + 1;
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
        var approximateCount = Volatile.Read(ref _approximateCount);
        Debug.Assert(approximateCount >= 0);
        if (approximateCount == 0)
            return null;

        lock (_lock)
        {
            if (!_stack.TryPop(out var item))
                return null;
            _approximateCount--;
            return item;
        }
    }

    /// <summary>Approximate item count. Acquires the lock for an exact snapshot.</summary>
    public readonly int Count
    {
        get
        {
            lock (_lock)
                return _stack.Count;
        }
    }

    /// <summary>Approximate emptiness check.</summary>
    public readonly bool IsEmpty
    {
        get
        {
            lock (_lock)
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
            lock (_queue._lock)
            {
                while (_queue._stack.TryPop(out var item))
                    result.Add(item);
                _queue._approximateCount = 0;
            }

            return result;
        }
    }
}
