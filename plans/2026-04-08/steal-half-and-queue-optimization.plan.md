---
name: Steal-Half and Queue Optimization
overview: Implement Tokio/Go-style steal-half work distribution in the Chase-Lev deque, replace allocation-heavy ConcurrentStack/ConcurrentQueue with zero-allocation alternatives, and add a global overflow queue for the push-overflow path.
todos:
  - id: steal-half-deque
    content: Add TryStealHalf method to WorkStealingDeque<T> (CAS claim half of victim's items, copy into thief's buffer, return one for immediate execution)
    status: in_progress
  - id: steal-half-workerpool
    content: Update WorkerPool.TryStealAndExecute to call TryStealHalf instead of TrySteal, plumbing stolen items into thief's deque
    status: pending
  - id: bounded-stack
    content: Replace ConcurrentStack<int> _sleepers with System.Threading.Lock-guarded bounded int[] stack (zero allocation)
    status: pending
  - id: intrusive-queue
    content: Replace ConcurrentQueue<Job> _injectionQueue with System.Threading.Lock-guarded intrusive linked list (add NextInQueue field to Job, zero allocation)
    status: pending
  - id: tests
    content: Unit + stress tests for TryStealHalf, bounded stack, intrusive queue; verify all existing tests pass
    status: pending
  - id: benchmark
    content: Re-run RealisticTickBenchmark, compare against 11.87ms baseline, create PR with results
    status: pending
isProject: false
---

# Steal-Half Work Distribution and Queue Optimization

## Current Bottleneck

The `RealisticTickBenchmark` runs at **11.87ms** vs a theoretical minimum of ~0.42ms (~28x overhead). The DAG has 4 barrier fan-out events where N ready jobs are pushed to a **single** worker's deque:

- Trigger -> 64 Phase1 jobs
- Barrier1 -> 64 Phase2 jobs
- Barrier2 -> 32 Phase3 jobs
- Barrier3 -> 16 Phase4 jobs

With the current steal-one model, 15 idle workers must each individually CAS on that one deque's `_top`, interleaved with `Thread.Yield` (~100us on macOS). This creates a linear distribution bottleneck.

With **steal-half**, distribution becomes logarithmic: Worker B takes 32, C takes 16, D takes 16 from B, etc. After ~4 rounds (log2(16)), all workers have local work. **~4 CAS rounds instead of ~64.**

## Part 1: Steal-Half for WorkStealingDeque

Add a `TryStealHalf(WorkStealingDeque<T> destination, out T firstItem)` method to [`WorkStealingDeque<T>`](src/Fabrica.Core/Threading/Queues/WorkStealingDeque.cs).

**Algorithm** (adapted from Tokio's `steal_into2` in `queue.rs`):

```csharp
public bool TryStealHalf(WorkStealingDeque<T> destination, out T firstItem)
{
    // 1. Read victim's top and bottom
    var top = Volatile.Read(ref _top);
    Thread.MemoryBarrier();
    var bottom = Volatile.Read(ref _bottom);

    var available = bottom - top;
    if (available <= 0) { firstItem = default!; return false; }

    // 2. Compute steal count: half (rounded up), but leave at least 1 for the owner
    var n = available - available / 2;  // steal ceil(available/2)

    // 3. CAS victim's _top to claim the range [top, top+n)
    if (Interlocked.CompareExchange(ref _top, top + n, top) != top)
    { firstItem = default!; return false; }

    // 4. Copy n-1 items into destination's buffer (destination is owned by the thief)
    //    Return the first item directly for immediate execution
    var srcBuffer = Volatile.Read(ref _buffer);
    firstItem = srcBuffer.Items[top & srcBuffer.Mask];

    if (n > 1)
    {
        var dstBottom = destination._bottom; // safe: thief is the single producer
        var dstBuffer = destination._buffer;
        // Grow destination if needed...
        for (var i = 1; i < n; i++)
        {
            var srcIdx = (top + i) & srcBuffer.Mask;
            var dstIdx = (dstBottom + i - 1) & dstBuffer.Mask;
            dstBuffer.Items[dstIdx] = srcBuffer.Items[srcIdx];
        }
        Volatile.Write(ref destination._bottom, dstBottom + n - 1);
    }
    return true;
}
```

**Key correctness points:**
- The CAS on `_top` atomically claims the range, identical to the existing single-item steal
- Writing to `destination._bottom` and `destination._buffer` is safe because the thief IS the single producer of its own deque
- The owner (victim) can still push to `_bottom` concurrently -- these are disjoint regions
- Other concurrent stealers will see the updated `_top` and either steal from the remaining half or fail their CAS (standard retry)

**Tokio's packed-head approach**: Tokio packs `(steal_head, real_head)` into one atomic to prevent other stealers from touching the range being copied. We can achieve equivalent safety by simply accepting that another concurrent stealer's CAS may fail during our copy window (which already happens with steal-one). If this proves to be a contention issue in practice, we can adopt the packed-head approach as a follow-up.

### Update WorkerPool.TryStealAndExecute

In [`WorkerPool.cs`](src/Fabrica.Core/Jobs/WorkerPool.cs), change `TryStealAndExecute` to use `TryStealHalf`:

```csharp
private bool TryStealAndExecute(WorkerContext context)
{
    var count = _allContexts.Length;
    var start = (int)context.StealRand.NextN((uint)count);
    for (var i = 0; i < count; i++)
    {
        var target = _allContexts[(start + i) % count];
        if (target.WorkerIndex == context.WorkerIndex) continue;

        if (target.Deque.TryStealHalf(context.Deque, out var job))
        {
            this.ExecuteJob(job, context);
            return true;
        }
    }
    return false;
}
```

The stolen half (minus the first item) lands in the thief's own deque, making them available to be stolen again by further idle workers (cascade distribution).

---

## Part 2: Replace ConcurrentStack with Zero-Allocation Bounded Stack

**Problem**: .NET's `ConcurrentStack<T>.Push` allocates a `new Node(item)` on every call (a `sealed class` with `_value` + `_next` fields). For `_sleepers`, this means a heap allocation every time a worker parks -- which is hot during inter-phase transitions.

**Solution**: A fixed-size array + atomic count. Max workers is 127, so this is trivially bounded.

```csharp
// Replaces ConcurrentStack<int> _sleepers
private readonly int[] _sleeperArray;  // length = _backgroundWorkerCount
private int _sleeperTop;               // atomic via Interlocked

// Push (called by parking worker -- single writer per value, but multiple concurrent pushers)
private void PushSleeper(int workerIndex)
{
    // Atomically claim a slot
    var slot = Interlocked.Increment(ref _sleeperTop) - 1;
    _sleeperArray[slot] = workerIndex;
    // Release fence to ensure the write is visible before a consumer reads
}

// Pop (called by TryWakeOneWorker -- may be called concurrently)
private bool TryPopSleeper(out int workerIndex)
{
    while (true)
    {
        var top = Volatile.Read(ref _sleeperTop);
        if (top <= 0) { workerIndex = -1; return false; }
        if (Interlocked.CompareExchange(ref _sleeperTop, top - 1, top) == top)
        {
            workerIndex = _sleeperArray[top - 1];
            return true;
        }
    }
}
```

**Trade-off vs a simple `lock`**: The lock approach (what Tokio does) is simpler and provably correct. The lock-free approach above has a subtle race: after `Interlocked.Increment` but before the array write completes, a concurrent `TryPopSleeper` could read an uninitialized slot. This can be solved with a per-slot ready flag or by just using `lock`. Given the critical section is ~2 instructions, `lock` (`Monitor.Enter`/`Exit`) is ~20ns uncontended on modern .NET and is the safer choice.

**Recommendation**: Use `System.Threading.Lock` + `int[]` + `int count`. `System.Threading.Lock` (available since .NET 9; we're on .NET 10) is purpose-built for locking — no sync block dual-role overhead, no ThinLock-to-SyncBlock escalation, `EnterScope()` returns a stack-allocated `ref struct`. Faster than `Monitor.Enter`/`Exit` in the uncontended case, which is the only case that matters here (during active gameplay, workers stay in WarmYield and never park, so `_sleepers` is never accessed — see contention analysis below). Zero allocations, trivially correct.

**Contention analysis**: During active gameplay, phase transitions happen every few ms. Workers exhaust their jobs, enter HotSpin/WarmYield (counted as "searching"), and find new work when the next barrier fans out — well before the 1000ms KeepAliveMs threshold. `_sleepers` Push/Pop are only reached during truly idle periods (loading screens, >1s of no work). So contention is effectively zero.

---

## Part 3: Replace ConcurrentQueue with Intrusive Linked List (Global Overflow Queue)

**Current state**: `ConcurrentQueue<Job>` is used for `_injectionQueue` (test path only in production). But with steal-half, we want a **global overflow queue** (Tokio's model): when `PropagateCompletion` readies more jobs than the local deque can hold, overflow goes here.

**Tokio's approach**: An intrusive linked list guarded by a mutex. "Intrusive" means the node pointer lives inside the task itself (no separate Node allocation). The critical section is just a pointer update.

**Implementation**:

1. Add a field to [`Job`](src/Fabrica.Core/Jobs/Job.cs):
   ```csharp
   internal Job? NextInQueue;
   ```

2. Replace `_injectionQueue` with:
   ```csharp
   private readonly Lock _globalQueueLock = new();
   private Job? _globalQueueHead;
   private Job? _globalQueueTail;
   private int _globalQueueCount;
   ```

3. **PushBatch** (for overflow from PropagateCompletion):
   ```csharp
   // Pre-link the batch OUTSIDE the lock (O(N) but no contention)
   // Then acquire lock and append to tail (O(1) critical section)
   ```

4. **TryDequeue**:
   ```csharp
   // Acquire lock, remove head (O(1) critical section)
   ```

**Comparison with ConcurrentQueue**:
- ConcurrentQueue uses 32-element segments with complex CAS-based slot management, volatile reads on every operation, and segment allocation when growing
- Intrusive list: zero allocations (pointer lives in Job), `System.Threading.Lock` critical section is ~2 pointer writes
- For low-contention single-item dequeue (which is what workers do -- they only check global queue when local + steal fail), the lock is essentially free
- Same contention analysis as `_sleepers`: workers check the global queue LAST (after local pop and steal-half from peers), so contention is minimal

---

## Part 4: Overflow from PropagateCompletion

Currently, `PropagateCompletion` pushes ALL readied dependents to the current worker's deque. With steal-half, this is less of a bottleneck (stealers take half at a time). But we should add the Tokio overflow path as well:

- If the worker's deque is nearly full after pushing, move half to the global queue
- This matches Tokio's `push_back_or_overflow` pattern
- Keeps local deque bounded (currently it can grow unboundedly via `Grow()`)

This can be a follow-up optimization. The primary win comes from steal-half.

---

## Part 5: Testing

1. **Unit tests for TryStealHalf**: Single-threaded deterministic tests (victim has N items, thief steals half, verify counts and values). Edge cases: 0 items, 1 item, 2 items, power-of-2 boundary.
2. **Stress tests**: Multi-threaded producer + multiple stealers using TryStealHalf. Verify no items lost or duplicated.
3. **Bounded stack tests**: Push/pop correctness, concurrent push+pop, empty pop returns false.
4. **Intrusive queue tests**: Single-item and batch push, concurrent enqueue/dequeue, empty dequeue.
5. **Existing tests must pass**: All `WorkStealingDequeTests`, `WorkStealingDequeStressTests`, `JobSchedulerTests`.
6. **Benchmark**: Re-run `RealisticTickBenchmark` and compare against baseline (11.87ms).

---

## Expected Impact

The primary bottleneck is inter-phase distribution latency. With 4 barrier fan-outs and 16 cores:
- **Steal-one**: ~64 sequential CAS ops per fan-out, each gated by OS yield latency
- **Steal-half**: ~4 rounds of CAS ops per fan-out, on progressively more deques (reduced contention)

Conservative estimate: 3-5x improvement on phase transition overhead, which is the dominant cost. The bounded stack and intrusive queue are correctness/allocation improvements that support zero-allocation steady state.

## Implementation Order

The steal-half change to `WorkStealingDeque` and `WorkerPool.TryStealAndExecute` is the highest-impact change and can be done independently. The bounded stack and intrusive queue are lower-risk improvements that can be done in parallel or sequentially.
