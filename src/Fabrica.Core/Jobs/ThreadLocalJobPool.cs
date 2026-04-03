using Fabrica.Core.Collections;
using Fabrica.Core.Memory;

namespace Fabrica.Core.Jobs;

/// <summary>
/// Per-thread object pool for <see cref="Job"/> instances backed by <see cref="WorkStealingDeque{T}"/>. Each thread
/// owns a deque that it pushes to (Return) and pops from (Rent) as owner-only operations — no CAS, no locks on the
/// hot path. Cross-thread rents use the deque's lock-free <see cref="WorkStealingDeque{T}.TrySteal"/> operation, which
/// is safe to call concurrently with the owner's push and pop.
///
/// THREAD MODEL
///   <see cref="Return"/> calls <see cref="WorkStealingDeque{T}.Push"/> on the caller's deque (owner operation — a
///   store + volatile write, no CAS).
///
///   <see cref="Rent"/> first calls <see cref="WorkStealingDeque{T}.TryPop"/> on the caller's deque (owner operation —
///   typically no CAS). If the deque is empty, it round-robin steals from other threads' deques via
///   <see cref="WorkStealingDeque{T}.TrySteal"/> (lock-free, safe to call concurrently at any time).
///
///   No fork/join phase restrictions — any thread can steal from any other thread's deque at any time.
///
/// DESIGN
///   The pool is per-type: each concrete <typeparamref name="TJob"/> (e.g., <c>SimulateChunkJob</c>) has its own
///   <see cref="ThreadLocalJobPool{TJob, TAllocator}"/> instance. Callers pass their thread index to each operation.
///
///   The <typeparamref name="TAllocator"/> is constrained to struct so the JIT specialises every call, eliminating all
///   interface dispatch in the hot path. The allocator's <see cref="IAllocator{T}.Reset"/> is called on
///   <see cref="Return"/> before pushing to the deque.
///
/// ROUND-ROBIN STEALING
///   When a thread's own deque is empty, <see cref="Rent"/> scans other deques starting from a rotating index. This
///   distributes steal pressure evenly across all threads instead of always draining lower-indexed deques first.
///
/// LIFECYCLE
///   In a fork-join loop, items naturally flow: coordinator rents from its deque (or steals from worker deques),
///   workers execute and return to their own deques. After steady-state warmup, allocations drop to zero because each
///   worker's deque holds enough items for its share of the batch.
/// </summary>
public sealed class ThreadLocalJobPool<TJob, TAllocator>
    where TJob : Job
    where TAllocator : struct, IAllocator<TJob>
{
    private readonly WorkStealingDeque<TJob>[] _deques;

    /// <summary>
    /// Rotating index for round-robin steal scanning. Not volatile — it's a hint for fairness, not a correctness
    /// mechanism. A stale read just means scanning starts from a slightly different position.
    /// </summary>
    private int _nextStealIndex;

    /// <summary>Creates a pool with the specified number of per-thread deques.</summary>
    public ThreadLocalJobPool(int threadCount)
    {
        _deques = new WorkStealingDeque<TJob>[threadCount];
        for (var i = 0; i < threadCount; i++)
            _deques[i] = new WorkStealingDeque<TJob>();
    }

    /// <summary>The number of per-thread slots in this pool.</summary>
    public int ThreadCount => _deques.Length;

    /// <summary>
    /// Returns a pooled instance if available, or allocates a new one via the <typeparamref name="TAllocator"/>.
    /// Checks the caller's own deque first (owner TryPop — no CAS typically), then round-robin steals from other
    /// deques (lock-free TrySteal), then allocates if all deques are empty.
    /// </summary>
    public TJob Rent(int threadIndex)
    {
        if (_deques[threadIndex].TryPop(out var job))
            return job;

        var startIndex = _nextStealIndex;
        for (var offset = 0; offset < _deques.Length; offset++)
        {
            var stealIndex = (startIndex + offset) % _deques.Length;
            if (stealIndex == threadIndex)
                continue;

            if (_deques[stealIndex].TrySteal(out job))
            {
                _nextStealIndex = (stealIndex + 1) % _deques.Length;
                return job;
            }
        }

        return default(TAllocator).Allocate();
    }

    /// <summary>
    /// Resets the job via <see cref="IAllocator{T}.Reset"/> and pushes it onto the specified thread's deque. The
    /// caller must be the owning thread for this slot (enforced by debug assertions in the deque).
    /// </summary>
    public void Return(int threadIndex, TJob item)
    {
        default(TAllocator).Reset(item);
        _deques[threadIndex].Push(item);
    }

    /// <summary>
    /// Total number of pooled items across all per-thread deques. Not linearizable — intended for diagnostics and
    /// testing only.
    /// </summary>
    public int Count
    {
        get
        {
            var count = 0L;
            foreach (var deque in _deques)
                count += deque.Count;

            return (int)count;
        }
    }

    /// <summary>Number of pooled items in a specific thread's deque. Not linearizable.</summary>
    public int CountForThread(int threadIndex)
        => (int)_deques[threadIndex].Count;

    // ═══════════════════════════ TEST ACCESSOR ═══════════════════════════════

    internal TestAccessor GetTestAccessor()
        => new(this);

    /// <summary>Provides internal access to the pool's deques for testing.</summary>
    internal struct TestAccessor(ThreadLocalJobPool<TJob, TAllocator> pool)
    {
        /// <summary>Access the underlying deque for a specific thread slot.</summary>
        public readonly WorkStealingDeque<TJob> GetDeque(int threadIndex)
            => pool._deques[threadIndex];

        /// <summary>The current round-robin steal start index.</summary>
        public readonly int NextStealIndex => pool._nextStealIndex;
    }
}
