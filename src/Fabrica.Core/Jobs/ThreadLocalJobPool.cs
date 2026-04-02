using System.Diagnostics;

namespace Fabrica.Core.Jobs;

/// <summary>
/// Per-thread object pool for <see cref="Job"/> instances. Each thread owns a private <see cref="Stack{T}"/> that it
/// pushes to and pops from without any synchronization — no locks, no CAS, no kernel transitions on the hot path.
///
/// THREAD MODEL
///   <see cref="Return"/> pushes to the caller's own stack. <see cref="Rent"/> pops from the caller's own stack first.
///   Both are single-threaded per slot and require no synchronization.
///
///   When a thread's own stack is empty, <see cref="Rent"/> scans other threads' stacks. This cross-thread access is
///   only safe when the target threads are idle — specifically, between fork-join phases when all workers have parked
///   after completing their batch. In DEBUG builds, thread-ownership assertions catch accidental concurrent access.
///
/// DESIGN
///   The pool is per-type: each concrete <typeparamref name="T"/> (e.g., <c>SimulateChunkJob</c>) has its own
///   <see cref="ThreadLocalJobPool{T}"/> instance. Callers pass their thread index to each operation.
///
///   Unlike <see cref="JobPool{T}"/>, this design uses standard <see cref="Stack{T}"/> instead of an intrusive linked
///   list, so the <see cref="Job"/> base class does not need a next-pointer field.
///
/// LIFECYCLE
///   In a fork-join loop, items naturally flow: coordinator rents from its stack (or steals from idle worker stacks),
///   workers execute and return to their own stacks. After steady-state warmup, allocations drop to zero because each
///   worker's stack holds enough items for its share of the batch.
///
/// SAFETY INVARIANT
///   Cross-thread scanning in <see cref="Rent"/> is only safe when the scanned threads are idle. In the fork-join
///   model this is naturally satisfied: the coordinator rents jobs before submitting them, at which point all workers
///   are parked waiting for work.
/// </summary>
public sealed class ThreadLocalJobPool<T> where T : Job, new()
{
    private readonly Stack<T>[] _stacks;

#if DEBUG
    private readonly int[] _ownerThreadIds;

    private void AssertOwnerThread(int threadIndex)
    {
        var current = Environment.CurrentManagedThreadId;
        if (_ownerThreadIds[threadIndex] == -1)
            _ownerThreadIds[threadIndex] = current;
        else
            Debug.Assert(
                _ownerThreadIds[threadIndex] == current,
                $"ThreadLocalJobPool<{typeof(T).Name}> slot {threadIndex} accessed from thread {current} " +
                $"but owner is thread {_ownerThreadIds[threadIndex]}. Each slot is single-threaded.");
    }
#endif

    /// <summary>Creates a pool with the specified number of per-thread stacks.</summary>
    public ThreadLocalJobPool(int threadCount)
    {
        _stacks = new Stack<T>[threadCount];
        for (var i = 0; i < threadCount; i++)
            _stacks[i] = new Stack<T>();

#if DEBUG
        _ownerThreadIds = new int[threadCount];
        Array.Fill(_ownerThreadIds, -1);
#endif
    }

    /// <summary>The number of per-thread slots in this pool.</summary>
    public int ThreadCount => _stacks.Length;

    /// <summary>
    /// Returns a pooled instance if available, or allocates a new one. Checks the caller's own stack first (zero
    /// contention), then scans other threads' stacks if empty.
    ///
    /// SAFETY: Cross-thread scanning is only safe when the target threads are idle (e.g., between fork-join phases).
    /// The caller must ensure this.
    /// </summary>
    public T Rent(int threadIndex)
    {
        if (_stacks[threadIndex].Count > 0)
            return _stacks[threadIndex].Pop();

        for (var i = 0; i < _stacks.Length; i++)
        {
            if (i != threadIndex && _stacks[i].Count > 0)
                return _stacks[i].Pop();
        }

        return new T();
    }

    /// <summary>
    /// Returns a job to the specified thread's stack. No synchronization — the caller must be the owning thread for
    /// this slot.
    /// </summary>
    public void Return(int threadIndex, T item)
    {
#if DEBUG
        this.AssertOwnerThread(threadIndex);
#endif
        _stacks[threadIndex].Push(item);
    }

    /// <summary>
    /// Total number of pooled items across all per-thread stacks. Not linearizable when threads are actively
    /// returning — intended for diagnostics and testing only when threads are idle.
    /// </summary>
    public int Count
    {
        get
        {
            var count = 0;
            foreach (var stack in _stacks)
                count += stack.Count;

            return count;
        }
    }

    /// <summary>Number of pooled items in a specific thread's stack.</summary>
    public int CountForThread(int threadIndex)
        => _stacks[threadIndex].Count;
}
