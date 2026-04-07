namespace Fabrica.Core.Jobs;

/// <summary>
/// Lock-free thread-safe object pool for <see cref="Job"/> instances, implemented as an intrusive
/// Treiber stack.
///
/// THREAD SAFETY
///   Any thread may call <see cref="Rent"/> or <see cref="Return"/> concurrently. Both operations
///   use a single <see cref="Interlocked.CompareExchange{T}"/> on the stack head — no locks, no
///   kernel transitions.
///
/// DESIGN
///   The pool is per-type: each concrete <typeparamref name="TJob"/> has its own
///   <see cref="JobPool{TJob}"/> instance. This avoids type checks on rent/return and keeps the
///   pool homogeneous.
///
///   The intrusive approach stores the next-pointer directly in the <see cref="Job.PoolNext"/>
///   field, so pooled items require zero additional allocation for list nodes.
///
///   When the pool is empty, <see cref="Rent"/> allocates a new instance via the
///   <c>new()</c> constraint. The pool never pre-allocates — it warms up naturally as jobs
///   complete and are returned.
///
/// CONTENTION
///   In the typical pattern, renting happens on one or a few threads (coordinator and parent
///   jobs) while returning is done by the coordinator during the sweep. The CAS loop rarely
///   retries because operations are temporally dispersed relative to the cost of job execution.
///
/// UNBOUNDED
///   The pool grows without bound. In steady state, the pool size stabilizes at the maximum
///   number of concurrently live jobs of this type. If this becomes a concern, a bounded variant
///   can cap the pool size and let excess items fall to GC.
///
/// PORTABILITY
///   Uses <see cref="Interlocked.CompareExchange{T}"/> for the CAS. In Rust: an intrusive
///   <c>AtomicPtr</c> stack. In C++: <c>std::atomic&lt;Node*&gt;</c> with compare_exchange_weak.
/// </summary>
internal sealed class JobPool<TJob> where TJob : Job, new()
{
    private TJob? _head;

#if DEBUG
    /// <summary>Called in <see cref="Rent"/> after reading head but before the CAS that pops it.
    /// Tests can inject a concurrent operation here to force the CAS-lost retry path.</summary>
    internal Action? DebugBeforeRentCas { get; set; }

    /// <summary>Called in <see cref="Return"/> after setting PoolNext but before the CAS that
    /// pushes the item. Tests can inject a concurrent operation here to force the CAS-lost
    /// retry path.</summary>
    internal Action? DebugBeforeReturnCas { get; set; }
#endif

    /// <summary>
    /// Returns a pooled instance if available, or allocates a new one. The returned job is in a
    /// clean state (reset during the previous return) — the caller must configure
    /// DAG dependencies (via <see cref="Job.DependsOn"/>) and subclass-specific fields
    /// before submitting.
    /// </summary>
    public TJob Rent()
    {
        var spinner = new SpinWait();

        while (true)
        {
            var head = Volatile.Read(ref _head);
            if (head is null)
                return new TJob();

            var next = (TJob?)head.PoolNext;

#if DEBUG
            this.DebugBeforeRentCas?.Invoke();
#endif

            if (Interlocked.CompareExchange(ref _head, next, head) == head)
            {
                head.PoolNext = null;
                return head;
            }

            spinner.SpinOnce();
        }
    }

    /// <summary>
    /// Resets the job via <see cref="Job.Reset"/> and returns it to the pool for reuse. May be
    /// called from any thread, though in the standard pattern the coordinator calls this during
    /// the DAG sweep.
    /// </summary>
    public void Return(TJob item)
    {
        item.Scheduler = null;
#if DEBUG
        item.State = default;
#endif
        item.Reset();

        var spinner = new SpinWait();

        while (true)
        {
            var head = Volatile.Read(ref _head);
            item.PoolNext = head;

#if DEBUG
            this.DebugBeforeReturnCas?.Invoke();
#endif

            if (Interlocked.CompareExchange(ref _head, item, head) == head)
                return;

            spinner.SpinOnce();
        }
    }

    /// <summary>
    /// Approximate number of pooled items. Not linearizable — a concurrent Rent or Return can
    /// change the stack between node traversals. Intended for diagnostics and testing only.
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
                current = (TJob?)current.PoolNext;
            }

            return count;
        }
    }

    // ── Test accessor ─────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor()
        => new(this);

    internal struct TestAccessor(JobPool<TJob> pool)
    {
        public readonly TJob? Head => Volatile.Read(ref pool._head);
    }
}
