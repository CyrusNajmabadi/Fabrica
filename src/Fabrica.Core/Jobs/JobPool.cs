using Fabrica.Core.Memory;

namespace Fabrica.Core.Jobs;

/// <summary>
/// Lock-free thread-safe object pool for <see cref="Job"/> instances, implemented as an intrusive Treiber stack.
///
/// THREAD SAFETY
///   Any thread may call <see cref="Rent"/> or <see cref="Return"/> concurrently. Both operations use a single
///   <see cref="Interlocked.CompareExchange{T}"/> on the stack head — no locks, no kernel transitions.
///
/// DESIGN
///   The pool is per-type: each concrete <typeparamref name="TJob"/> (e.g., <c>SimulateChunkJob</c>) has its own
///   static <see cref="JobPool{TJob, TAllocator}"/> instance. This avoids type checks on rent/return and keeps the
///   pool homogeneous.
///
///   The intrusive approach stores the next-pointer directly in the <see cref="Job._poolNext"/> field, so pooled items
///   require zero additional allocation for list nodes.
///
///   When the pool is empty, <see cref="Rent"/> allocates a new instance via the <typeparamref name="TAllocator"/>.
///   The allocator is constrained to struct so the JIT specialises every call, eliminating all interface dispatch. The
///   pool never pre-allocates — it warms up naturally as jobs complete and return.
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
public sealed class JobPool<TJob, TAllocator>
    where TJob : Job
    where TAllocator : struct, IAllocator<TJob>
{
    /// <summary>Head of the intrusive linked-list stack. Null when the pool is empty.</summary>
    private TJob? _head;

    /// <summary>
    /// Returns a pooled instance if available, or allocates a new one via the <typeparamref name="TAllocator"/>. The
    /// returned job's fields are in a clean state (reset by the allocator on the previous return) — the caller must
    /// configure them before submitting.
    /// </summary>
    public TJob Rent()
    {
        var spinner = new SpinWait();

        while (true)
        {
            var head = Volatile.Read(ref _head);
            if (head is null)
                return default(TAllocator).Allocate();

            var next = (TJob?)head._poolNext;
            if (Interlocked.CompareExchange(ref _head, next, head) == head)
            {
                head._poolNext = null;
                return head;
            }

            spinner.SpinOnce();
        }
    }

    /// <summary>
    /// Resets the job via <see cref="IAllocator{T}.Reset"/> and returns it to the pool for reuse. May be called from
    /// any thread.
    /// </summary>
    public void Return(TJob item)
    {
        default(TAllocator).Reset(item);

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
                current = (TJob?)current._poolNext;
            }

            return count;
        }
    }
}
