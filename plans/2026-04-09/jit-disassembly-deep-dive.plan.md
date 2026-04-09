---
name: JIT disassembly deep dive
overview: Build a JIT disassembly harness that exercises the full scheduler hot path, capture ARM64 assembly for every method on the critical path, then analyze each method in parallel for optimization opportunities.
todos:
  - id: harness
    content: Create disassembly harness console app that exercises the full benchmark DAG
    status: completed
  - id: capture
    content: Run harness with DOTNET_JitDisasm to capture ARM64 assembly for all hot-path methods
    status: completed
  - id: analyze
    content: Launch parallel agents to analyze each method's disassembly for optimization opportunities
    status: completed
  - id: report
    content: Consolidate findings into a ranked optimization roadmap
    status: completed
isProject: false
---

# JIT Disassembly Deep Dive

## Phase 1: Build the disassembly harness

Create a small console app (or extend an existing one) that:

1. Instantiates a `WorkerPool` + `JobScheduler` with the same DAG as `RealisticTickBenchmark` (trigger → 192 compute → barrier → 192 compute → barrier → 96 compute → barrier → 48 snapshot → collector)
2. Runs the DAG once to force JIT compilation of all hot-path methods
3. Exits cleanly

Run it with `DOTNET_JitDisasm` set to a broad filter that captures all methods we care about. The env var accepts wildcards, so we can use multiple filters joined by spaces:

```
DOTNET_JitDisasm="BoundedLocalQueue*:* WorkerPool:* WorkerContext:* JobScheduler:* InjectionQueue*:* ComputeJob:* BarrierJob:* TriggerJob:* SnapshotJob:* SnapshotCollectorJob:* JobContext:*"
```

This captures everything on the hot path in a single run. Redirect stderr to a file (JIT disasm goes to stderr on .NET).

## Phase 2: Target methods

The full set of methods to disassemble and analyze:

**Queue operations (BoundedLocalQueue):**

- `TryPop()`
- `Push(T)`
- `PushToRingBuffer(T)`
- `TryStealHalf(ref BoundedLocalQueue<T>)`
- `Pack`, `Unpack`, `Distance` (should be inlined — verify)

**Injection queue:**

- `InjectionQueue<Job>.TryDequeue()`
- `InjectionQueue<Job>.Enqueue(T)`

**Scheduler dispatch:**

- `WorkerPool.TryExecuteOne(WorkerContext)`
- `WorkerPool.TryStealAndExecute(WorkerContext)`
- `WorkerPool.TryDequeueInjected(WorkerContext)`
- `WorkerPool.ExecuteJob(Job, WorkerContext)`
- `WorkerPool.PropagateCompletion(Job, WorkerContext)`

**Wake/idle management:**

- `WorkerPool.TryWakeOneWorker()`
- `WorkerPool.TransitionFromSearching(WorkerContext)`
- `WorkerPool.NotifyWorkAvailable()`

**Coordinator:**

- `JobScheduler.Submit(Job)`
- `JobScheduler.RunUntilComplete()`

**Worker loop:**

- `WorkerPool.RunWorker(WorkerContext)`

**Job execution (concrete):**

- `ComputeJob.Execute(JobContext)` — dominant workload
- `BarrierJob.Execute(JobContext)`
- `SnapshotJob.Execute(JobContext)`

**Infrastructure:**

- `JobContext` constructor
- `SingleThreadedOwner.AssertOwnerThread()` — should be stripped in Release, verify

## Phase 3: Parallel deep-dive analysis

Launch one dedicated agent per method group. Each agent receives the full ARM64 disassembly, the corresponding C# source, and an exhaustive checklist of what to look for. The mandate for every agent is:

**Goal: find every possible optimization that does not affect correctness in Release builds. Every single cycle matters. This code must be as absolutely fast as possible on the latest .NET runtime (10.0, ARM64).**

**Mindset:** Each agent must think like a systems programmer writing C/C++/Rust who happens to be using C#. Draw on optimization techniques from ALL languages — not just .NET idioms. If a trick from C++, Rust, or JVM applies here, use it. The .NET runtime's JIT is powerful but imperfect; we need to know its blind spots and work around them.

**Unsafe is on the table.** We can and should use `Unsafe.`* APIs, raw pointer arithmetic, `[SkipLocalsInit]`, manual bounds check elision — anything that removes overhead. The ONLY rule is that **Debug builds must still validate correctness** (e.g., keep bounds checks behind `#if DEBUG` or `Debug.Assert`, keep `SingleThreadedOwner` checks in Debug). Release builds are where every instruction counts.

**The checklist below is a starting point, NOT exhaustive.** Agents must go beyond it. If something looks suboptimal for ANY reason — even if it's not on this list — flag it.

**Research is strongly encouraged.** Agents should spawn research sub-agents that use web search and URL fetching to:

- Read the actual .NET runtime source code on GitHub ([https://github.com/dotnet/runtime](https://github.com/dotnet/runtime)) — especially the JIT compiler (`src/coreclr/jit/`), the GC write barrier implementation, the `Interlocked` intrinsic codegen for ARM64, and the `Span<T>`/`InlineArray` lowering paths
- Look up the latest .NET 10 JIT optimizations, known limitations, and workarounds
- Check ARM64 instruction latency/throughput tables (Apple M-series specific if available)
- Find real-world high-performance .NET code (e.g., Kestrel, System.IO.Pipelines, ArrayPool, Channel) for patterns that squeeze out every cycle
- Search for .NET performance blog posts, runtime issues, and PRs that document JIT codegen gaps and workarounds
- Cross-reference with optimization patterns from C++, Rust, and JVM ecosystems that may apply

The goal is to make decisions based on ground truth — not guesses about what the JIT does. When in doubt, read the runtime source.

### Checklist (starting point — go beyond this)

**Allocation elimination:**

- Any `new` that could be avoided (pooling, stack allocation, struct conversion)
- Any boxing (value types passed as interfaces, `object`, delegate captures)
- Any hidden allocations (closures, iterators, string formatting, LINQ, params arrays)
- Can anything be `stackalloc`, `Span<T>`, or `ref struct` instead of heap?

**GC write barriers:**

- Every `CORINFO_HELP_CHECKED_ASSIGN_REF` / `CORINFO_HELP_ASSIGN_REF` call — can the field be restructured to avoid storing managed references in hot paths?
- Can `T?` returns eliminate `out T` patterns (as we did in PR #178)?
- Can reference-type fields be replaced with indices, handles, or value types?

**Bounds checks:**

- Every `cmp` + `b.hs` / `b.lo` pattern that guards array/span access — can the JIT prove it's in range via `& Mask` or loop bounds?
- Does InlineArray indexing with `& 0xFF` (Mask) get the bounds check eliminated? If not, use `Unsafe.Add` to bypass it.
- We CAN elide bounds checks using `Unsafe.Add(ref MemoryMarshal.GetReference(span), index)` pattern — just keep a `Debug.Assert(index < length)` for safety in debug.
- Same for any array access where we can prove the index is valid.

**Memory ordering:**

- Every `ldar` / `stlr` / `dmb` — is each one strictly necessary for correctness?
- Are there paired acquire/release fences that could be relaxed to plain loads/stores?
- Are there `Volatile.Read`/`Volatile.Write` calls that could be plain reads/writes because the field is only accessed by the owner thread?

**Inlining:**

- Every `bl` (branch-link / call) instruction — should that callee have been inlined?
- Are `[MethodImpl(AggressiveInlining)]` hints missing where the JIT's heuristics fail?
- Are there small helper methods (Pack, Unpack, Distance) that appear as calls instead of inline code?

**Register pressure and spills:**

- How many `stp`/`ldp` to the stack frame? Excessive spills suggest the method is too complex for the register file.
- Can local variables be eliminated or combined to reduce pressure?
- Would splitting a method into smaller pieces improve register allocation?

**Virtual dispatch:**

- Is `Job.Execute(JobContext)` devirtualized? If Job subclasses are sealed, can the JIT prove the target?
- Can we use `[MethodImpl(AggressiveInlining)]` + sealed classes to enable devirt?
- Are there interface dispatch stubs that could be avoided?

**Struct codegen:**

- Does `BoundedLocalQueue<T>` being a large struct cause excessive copying anywhere?
- Are there defensive copies from `readonly` access?
- Does `JobContext` (wrapping `WorkerContext`) add overhead vs passing `WorkerContext` directly?

**CAS/atomic patterns:**

- Are CAS retry loops optimal? (load → compute → CAS → branch on failure)
- Is the JIT emitting `ldaxr`/`stlxr` pairs or full `casal`? Which is better for each case?
- Can any `Interlocked` operations be replaced with plain reads/writes where only one thread mutates?

**Span/InlineArray codegen:**

- Does `Span<T?> srcSpan = _buffer` generate unnecessary setup code (null checks, length computation)?
- Are `Span.Slice().CopyTo()` and `Span.Slice().Clear()` emitting optimal `memcpy`/`memset`?
- Is the JIT recognizing `Span.Clear()` as a `memset` intrinsic?

**Branch prediction and code layout:**

- Are hot paths (success cases) in the fall-through position?
- Are cold paths (overflow, steal failure, empty queue) in unlikely branches?
- Could `[MethodImpl(NoInlining)]` on cold helpers improve hot-path code density?

**Miscellaneous .NET tricks:**

- `Unsafe.AsRef`, `Unsafe.Add`, `Unsafe.ByteOffset` to bypass safety checks where we've proven correctness
- `[SkipLocalsInit]` to avoid zero-initialization of stack locals
- Const propagation — are constants like `QueueCapacity`, `Mask`, `OverflowBatchSize` fully folded?
- Are there unnecessary null checks the JIT is inserting? (e.g., on `this` for struct methods, on non-null fields)
- `MethodImpl(AggressiveOptimization)` — forces Tier-1 JIT immediately, skipping Tier-0 interpreter
- `[module: SkipLocalsInit]` at assembly level if all methods benefit
- Can we use `nint`/`nuint` instead of `int` to avoid sign-extension on 64-bit?
- Are there instruction sequences where a different C# expression would produce better ARM64? (e.g., `(uint)x < (uint)len` vs `x >= 0 && x < len`)

**Cross-language optimization patterns:**

- Data-oriented design: are we accessing memory in cache-line-friendly order?
- Branchless alternatives: can conditional moves (`csel`) replace branches in hot paths?
- Loop unrolling: are tight loops (like the overflow drain) candidates for manual unrolling?
- Prefetching: would `Unsafe.Prefetch` hints help for steal operations that touch cold cache lines?
- Power-of-2 tricks: are divisions being replaced with shifts? Are modulo operations using `& (N-1)` everywhere?

**The Debug-mode safety rule:**

Any unsafe optimization must have a corresponding `Debug.Assert` or `#if DEBUG` check that validates the invariant it relies on. This is how we maintain correctness guarantees while eliminating overhead in Release. Example pattern:

```csharp
Debug.Assert((uint)index < (uint)QueueCapacity);
ref var slot = ref Unsafe.Add(ref MemoryMarshal.GetReference(span), index);
```

### Agent output format

Each agent returns:

1. The full annotated disassembly with every concern flagged inline (instruction-level)
2. A numbered list of optimization suggestions, each with:
  - What the problem is (with the specific instruction sequence)
  - What the fix would be (C# code change)
  - Estimated impact (high / medium / low) with reasoning
  - Whether it affects correctness (must be NO)

## Phase 4: Consolidate and prioritize

Merge all agent findings into a single ranked list. Group by:

- **High impact**: likely measurable in the benchmark (e.g., eliminating a GC barrier on every job, removing bounds checks in the hot loop)
- **Medium impact**: reduces instruction count but may not move the benchmark needle alone
- **Low impact**: cleaner codegen, marginal improvements

This becomes the implementation roadmap for the next round of changes.