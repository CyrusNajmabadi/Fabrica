# PR 3: Job Representation — Option A vs Option B

Two branches off `master`, each containing the same `Job` base class but a different pooling strategy.
The user will compare them side-by-side to decide which approach to adopt.

Both options use the `IAllocator<TJob>` pattern (struct-constrained, JIT-specialized) for zero-overhead
allocation and reset. No `new()` constraint.

---

## Shared Across Both PRs

### `src/Fabrica.Core/Jobs/Job.cs` — Abstract base class

- `abstract void Execute()` — worker calls this
- `abstract void Return()` — worker calls after Execute; derived class delegates to its pool's Return method
- Option A adds `_poolNext` internal field for the intrusive Treiber stack; Option B does not need it

---

## Option A: `JobPool<TJob, TAllocator>` — Lock-free shared Treiber stack

**Branch:** `feature/job-pool-option-a` | **PR:** #92

### `src/Fabrica.Core/Jobs/JobPool.cs`

- Single `_head` field; both `Rent()` and `Return()` use `Interlocked.CompareExchange` CAS loop with `SpinWait`
- Any thread can rent; any thread can return — fully thread-safe, no usage constraints
- Intrusive: uses `Job._poolNext` as the next pointer, so zero node allocations
- `Return` calls `IAllocator.Reset` before pushing to the stack
- `Count` property for diagnostics (not linearizable)
- Workers spawning sub-jobs works out of the box

**Tradeoffs:**
- Simple — one class, one file, no thread-index API
- CAS contention on `_head` when many workers return simultaneously (in practice dispersed and rare)
- Single cache line for `_head` bounces across cores

---

## Option B: `ThreadLocalJobPool<TJob, TAllocator>` — Per-thread WorkStealingDeques

**Branch:** `feature/job-pool-option-b` | **PR:** #93

### `src/Fabrica.Core/Jobs/ThreadLocalJobPool.cs`

- Each thread owns a `WorkStealingDeque<TJob>` — the same lock-free deque already in the codebase
- `Return(threadIndex, item)` → `Push` (owner operation — store + volatile write, no CAS)
- `Rent(threadIndex)` → `TryPop` from own deque first (owner operation — typically no CAS), then round-robin `TrySteal` from other deques (lock-free, safe concurrently with Push/TryPop)
- `Return` calls `IAllocator.Reset` before pushing to the deque
- Round-robin steal index distributes steal pressure evenly across threads
- No fork/join phase restrictions — TrySteal is concurrent-safe by design
- `Count` / `CountForThread` for diagnostics

**Tradeoffs:**
- Zero CAS on the hot path (Return + same-thread Rent)
- Each thread's deque is on a separate cache line — no cross-core invalidation on return
- More complex API — callers must know their thread index
- Reuses existing tested infrastructure (`WorkStealingDeque<T>`)
