# Research: How Engines Pool Job Objects

*Conducted 2026-04-03. Surveyed how major engines allocate and recycle job objects.*

---

## Summary

**No major engine uses a work-stealing deque as a pool backing store.** The deque is universally
used for scheduling (pending tasks), not for memory pooling (free objects). Object pooling uses
simpler structures.

---

## Engine-by-Engine Findings

### Unity (C# Job System + Burst)

- Jobs are **blittable value types** (`IJob` structs), not heap-allocated classes.
- Memory comes from **native allocators**, not managed GC heap:
  - `Allocator.Temp`: Per-thread, stack-like, recycled each frame. 256 KB per worker thread.
  - `Allocator.TempJob`: Linear allocator for job-to-job data. 4-frame dispose rule.
  - `Allocator.Persistent`: Long-lived, slower.
- **No object pool for jobs** — structs live in native memory managed by allocator kind.

### Naughty Dog

- Fixed **fiber pool** (160 fibers, two stack sizes) — pooling of execution contexts, not job data.
- Jobs run inside fibers; yielding reuses fibers from pool.
- Job data itself is not individually pooled — lives in frame-scoped memory.

### Jolt Physics

- **`FixedSizeFreeList`**: Lock-free construct/destruct of fixed-size objects.
- **`JobSystemThreadPool`** ties `inMaxJobs` to a maximum pool size.
- **Concrete example** of fixed freelist + cap for job objects in a game engine component.
- PR #1136 reported reordering fields to reduce false sharing contention (~5-15% improvement).

### Godot 4

- `WorkerThreadPool`: Worker threads created up front, native task/group APIs.
- Docs warn that very small tasks may not be worth the overhead.
- No user-visible job struct pool.

### Bevy (Rust)

- `bevy_tasks` with Compute/IO/AsyncCompute pools.
- PR #12869 reduced excess task spawning — scheduler shape matters as much as allocation.
- Rust ecosystem uses work-stealing executors for ready work, not for memory pooling.

---

## Pooling Patterns (Cross-Engine)

| Pattern | Where Used | Notes |
|---------|-----------|-------|
| Per-thread bump/stack allocators | Unity `Temp` | Frame recycle, minimal contention |
| Fixed-size free list | Jolt `FixedSizeFreeList` | Single ceiling, predictable memory |
| Per-thread work queues + stealing | Chase-Lev everywhere | For scheduling, NOT pooling |
| Frame arenas (bump + reset) | Unity, game middleware | O(1) reset, uniform lifetime |

---

## Key Takeaway

- **For scheduling (execution deque)**: Work-stealing deque is the right choice.
- **For pooling (object reuse)**: Simple per-thread freelists or shared Treiber stacks.
  The deque's steal CAS solves load balancing, not allocation.
- **For .NET specifically**: Pooling small short-lived objects may not help — Gen0 is efficient.
  Microsoft's guidance: measure first, pool only if allocation cost is high.

---

## Sources

- Unity Manual: Unmanaged C# memory (docs.unity3d.com)
- Jolt Physics: FixedSizeFreeList (jrouwe.github.io)
- Godot: WorkerThreadPool class reference
- Bevy: bevy_tasks docs, PR #12869
- Microsoft: Object reuse with ObjectPool (learn.microsoft.com)
