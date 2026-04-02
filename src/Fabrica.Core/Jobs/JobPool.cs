namespace Fabrica.Core.Jobs;

/// <summary>
/// Lock-free thread-safe object pool for <see cref="Job"/> instances, implemented as an intrusive Treiber stack.
///
/// THREAD SAFETY
///   Any thread may call <see cref="Rent"/> or <see cref="Return"/> concurrently. Both operations use a single
///   <see cref="Interlocked.CompareExchange{T}"/> on the stack head — no locks, no kernel transitions.
///
/// DESIGN
///   The pool is per-type: each concrete <typeparamref name="T"/> (e.g., <c>SimulateChunkJob</c>) has its own static
///   <see cref="JobPool{T}"/> instance. This avoids type checks on rent/return and keeps the pool homogeneous.
///
///   The intrusive approach stores the next-pointer directly in the <see cref="Job._poolNext"/> field, so pooled items
///   require zero additional allocation for list nodes.
///
///   When the pool is empty, <see cref="Rent"/> allocates a new instance via <c>new T()</c>. The pool never
///   pre-allocates — it warms up naturally as jobs complete and return.
///
/// CONTENTION
///   In the typical work-stealing pattern, renting happens on one thread (the coordinator) and returning happens across
///   many worker threads. The CAS loop rarely retries because returns are spread across workers and are temporally
///   dispersed. Under synthetic worst-case contention (all threads returning simultaneously), the CAS loop remains
///   wait-free in practice — each thread's retry makes progress for another thread.
///
/// UNBOUNDED
///   The pool grows without bound. In steady state, the pool size stabilizes at the maximum number of concurrently live
///   jobs of this type, which is typically the batch size per frame. If this becomes a concern, a bounded variant can
///   cap the pool size and let excess items fall to GC.
/// </summary>
public sealed class JobPool<T> where T : Job, new()
{
    /// <summary>Head of the intrusive linked-list stack. Null when the pool is empty.</summary>
    private T? _head;

    /// <summary>
    /// Returns a pooled instance if available, or allocates a new one. The returned job's fields are in an undefined
    /// state — the caller must configure them before submitting.
    /// </summary>
    public T Rent()
    {
        var spinner = new SpinWait();

        while (true)
        {
            var head = Volatile.Read(ref _head);
            if (head is null)
                return new T();

            var next = (T?)head._poolNext;
            if (Interlocked.CompareExchange(ref _head, next, head) == head)
            {
                head._poolNext = null;
                return head;
            }

            spinner.SpinOnce();
        }
    }

    /// <summary>
    /// Returns a job to the pool for reuse. The caller must have already reset the job's fields. May be called from
    /// any thread.
    /// </summary>
    public void Return(T item)
    {
        var spinner = new SpinWait();

        while (true)
        {
            var head = Volatile.Read(ref _head);
            item._poolNext = head;

            if (Interlocked.CompareExchange(ref _head, item, head) == head)
                return;

            spinner.SpinOnce();
        }
    }

    /// <summary>
    /// Approximate number of pooled items. Not linearizable — a concurrent Rent or Return can change the stack between
    /// node traversals. Intended for diagnostics and testing only.
    /// </summary>
    public int Count
    {
        get
        {
            var count = 0;
            var current = Volatile.Read(ref _head);
            while (current is not null)
            {
                count++;
                current = (T?)current._poolNext;
            }

            return count;
        }
    }
}
