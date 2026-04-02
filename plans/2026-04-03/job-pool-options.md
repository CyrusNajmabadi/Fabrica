# PR 3: Job Representation — Option A vs Option B

Two branches off `master`, each containing the same `Job` base class but a different pooling strategy.
The user will compare them side-by-side to decide which approach to adopt.

---

## Shared Across Both PRs

### `src/Fabrica.Core/Jobs/Job.cs` — Abstract base class

- `_poolNext` internal field (intrusive linked-list pointer for the pool)
- `abstract void Execute()` — worker calls this
- `abstract void Return()` — worker calls after Execute; derived class resets fields and returns itself to its pool

---

## Option A: `JobPool<T>` — Lock-free shared Treiber stack

**Branch:** `feature/job-pool-option-a`

### `src/Fabrica.Core/Jobs/JobPool.cs`

- Single `_head` field; both `Rent()` and `Return()` use `Interlocked.CompareExchange` CAS loop with `SpinWait`
- Any thread can rent; any thread can return — fully thread-safe
- Intrusive: uses `Job._poolNext` as the next pointer, so zero node allocations
- `Count` property for diagnostics (not linearizable)

### `tests/Fabrica.Core.Tests/Jobs/JobPoolTests.cs`

- Unit tests: empty pool rent, rent-return-reuse, LIFO order, PoolNext cleared, Count tracking, full lifecycle
- Stress tests (Theory): concurrent rent/return with barrier synchronization, interleaved rent/return stabilization, one-producer many-consumers

**Tradeoffs:**
- Simple — one class, one file
- CAS contention on `_head` when many workers return simultaneously (in practice dispersed and rare)

---

## Option B: `ThreadLocalJobPool<T>` — Per-thread pools, zero contention on return

**Branch:** `feature/job-pool-option-b`

### `src/Fabrica.Core/Jobs/ThreadLocalJobPool.cs`

- Each worker thread gets its own non-thread-safe stack (just a `Stack<T>`)
- `Register(int threadIndex)` — called once per worker during pool startup, creates the per-thread stack
- `Rent(int threadIndex)` — pops from that thread's stack, or allocates new
- `Return(int threadIndex, T item)` — pushes onto that thread's stack, no CAS needed
- A `RentFromAny()` fallback for the coordinator: if its own stack is empty, linearly scan other threads' stacks (rare path — only matters during warmup)
- `Count` property sums all per-thread stacks (diagnostic only)

### `tests/Fabrica.Core.Tests/Jobs/ThreadLocalJobPoolTests.cs`

- Unit tests: register thread, rent/return from same thread, LIFO order, rent from other thread's pool, Count across threads
- Stress tests: each thread rents/returns to its own index (no contention), coordinator renting from worker pools

**Tradeoffs:**
- Zero contention on the hot path (return after execute)
- More complex API — callers must know their thread index
- Cross-thread rent (coordinator renting during setup) needs special handling
- More state to manage (array of stacks)
