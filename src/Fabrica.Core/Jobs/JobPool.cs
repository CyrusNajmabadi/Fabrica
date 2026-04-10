// Algorithm ported from moodycamel::ConcurrentQueue by Cameron Desrochers.
// Original: https://github.com/cameron314/concurrentqueue
// License: Simplified BSD / Boost Software License 1.0 (dual-licensed).
// See THIRD-PARTY-NOTICES.md at the repository root for the full license text.

namespace Fabrica.Core.Jobs;

/// <summary>
/// Lock-free thread-safe object pool for <see cref="Job"/> instances, implemented as an intrusive
/// free list with per-node reference counting to solve the ABA problem.
///
/// ALGORITHM
///   Based on cameron314's ABA-safe lock-free free list from moodycamel/concurrentqueue.
///   Each node carries a <see cref="Job.FreeListRefs"/> int32 that packs a 31-bit reference
///   count (low bits) and a 1-bit SHOULD_BE_ON_FREELIST flag (sign bit). The reference count
///   prevents the classic ABA: a thread increments the refcount before reading PoolNext, ensuring
///   no other thread can modify PoolNext while the refcount is non-zero. The flag handles the
///   case where Return wants to add a node whose refcount is still non-zero — it defers the
///   actual list insertion to whichever thread decrements the refcount to zero.
///
/// THREAD SAFETY
///   Any thread may call <see cref="Rent"/> or <see cref="Return"/> concurrently. All operations
///   use atomic CAS on the stack head and atomic fetch-add on per-node refcounts — no locks, no
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
/// UNBOUNDED
///   The pool grows without bound. In steady state, the pool size stabilizes at the maximum
///   number of concurrently live jobs of this type. If this becomes a concern, a bounded variant
///   can cap the pool size and let excess items fall to GC.
/// </summary>
internal sealed class JobPool<TJob> where TJob : Job, IPoolableJob<TJob>
{
    // Sign bit used as "this node should be on the free list" flag.
    // Low 31 bits are the reference count.
    private const int SHOULD_BE_ON_FREELIST = unchecked((int)0x80000000); // int.MinValue
    private const int REFS_MASK = 0x7FFFFFFF; // int.MaxValue

    private readonly JobScheduler _scheduler;
    private TJob? _head;

    public JobPool(JobScheduler scheduler)
    {
        _scheduler = scheduler;
    }

    /// <summary>
    /// Returns a pooled instance if available, or allocates a new one. The returned job is in a
    /// clean state (reset during the previous return) — the caller must configure
    /// DAG dependencies (via <see cref="Job.DependsOn"/>) and subclass-specific fields
    /// before submitting.
    /// </summary>
    public TJob Rent()
    {
        while (true)
        {
            var head = Volatile.Read(ref _head);
            if (head is null)
                return TJob.Create(_scheduler);

            var prevHead = head;

            // Try to increment the refcount via CAS (not fetch-add) so we can check that the
            // refcount is non-zero before committing. If zero, the node is being moved between
            // threads and we must not touch its PoolNext.
            var refs = Volatile.Read(ref head.FreeListRefs);
            if ((refs & REFS_MASK) == 0 ||
                Interlocked.CompareExchange(ref head.FreeListRefs, refs + 1, refs) != refs)
            {
                continue;
            }

            // Refcount is now ≥2 (1 for the list + 1 for us). PoolNext is stable — no other
            // thread can modify it while refcount > 0.
            var next = (TJob?)head.PoolNext;

            if (Interlocked.CompareExchange(ref _head, next, head) == head)
            {
                // Successfully popped. Decrement refcount by 2: once for our ref, once for the
                // list's ref. The SHOULD_BE_ON_FREELIST flag must be clear since nobody else
                // knows it's been taken off yet.
                Interlocked.Add(ref head.FreeListRefs, -2);

                head.PoolNext = null;
                return head;
            }

            // Head CAS failed — another thread changed the head. Release our ref.
            // If after release the refcount hits zero AND the SHOULD_BE_ON_FREELIST flag is set,
            // we are responsible for adding this node back to the list.
            // Interlocked.Add returns the value AFTER the add. Before: SHOULD_BE_ON_FREELIST + 1,
            // after: SHOULD_BE_ON_FREELIST + 0.
            var after = Interlocked.Add(ref prevHead.FreeListRefs, -1);
            if (after == SHOULD_BE_ON_FREELIST)
            {
                this.AddKnowingRefcountIsZero(prevHead);
            }
        }
    }

    /// <summary>
    /// Resets the job via <see cref="Job.Reset"/> and returns it to the pool for reuse. May be
    /// called from any thread, though in the standard pattern the coordinator calls this during
    /// the DAG sweep.
    /// </summary>
    public void Return(TJob item)
    {
#if DEBUG
        item.State = default;
#endif
        item.Reset();

        // Set the SHOULD_BE_ON_FREELIST flag via fetch-add. If the refcount was zero before
        // (i.e. the result equals just the flag), we can add immediately. Otherwise, whichever
        // thread drops the refcount to zero will see the flag and do the add.
        var after = Interlocked.Add(ref item.FreeListRefs, SHOULD_BE_ON_FREELIST);
        if (after == SHOULD_BE_ON_FREELIST)
        {
            this.AddKnowingRefcountIsZero(item);
        }
    }

    /// <summary>
    /// Adds a node to the free list, knowing that its refcount is currently zero (so we have
    /// exclusive write access to its PoolNext). Sets refcount to 1 (the list's reference) and
    /// attempts a CAS. If the CAS fails, decrements back and sets the SHOULD_BE_ON_FREELIST
    /// flag, deferring the add to whichever thread next brings the refcount to zero.
    /// </summary>
    private void AddKnowingRefcountIsZero(TJob node)
    {
        var head = Volatile.Read(ref _head);
        while (true)
        {
            node.PoolNext = head;

            // Set refcount to 1 (the list's reference). Must be visible before the CAS publishes
            // the node as head.
            Volatile.Write(ref node.FreeListRefs, 1);

            var prev = Interlocked.CompareExchange(ref _head, node, head);
            if (prev == head)
                return;

            // CAS failed — head changed. Another thread may have already incremented our
            // refcount (saw it at 1, CAS'd to 2). We set SHOULD_BE_ON_FREELIST and subtract 1
            // in one step: fetch_add(SHOULD_BE_ON_FREELIST - 1). If the result is just the flag
            // (refcount portion zero), nobody else holds a ref and we can retry immediately.
            head = prev;
            var after = Interlocked.Add(ref node.FreeListRefs, unchecked(SHOULD_BE_ON_FREELIST - 1));
            if (after == SHOULD_BE_ON_FREELIST)
            {
                head = Volatile.Read(ref _head);
                continue;
            }

            // Another thread holds a reference — they will add the node when they release it.
            return;
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
                if (count > 1_000_000)
                    break; // Safety valve against cycles during debugging
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
