# Research: Arena-Backed Persistent Data Structures for Fabrica

*Conducted 2026-04-03. Synthesis of generational arenas, ECS struct-of-arrays layouts, deferred reference counting, slab
allocation, and handle-based object systems — the research basis for Fabrica’s arena-backed persistent structure design.*

**Related plan (implementation)**: `plans/2026-04-03/arena-persistent-structures.md`  
**Related research**: `deferred-reference-counting.md`, `synthesis-and-recommendations.md`

---

## 1. Problem Statement

Traditional **heap-allocated persistent (immutable, structurally shared) data structures** — lists, trees, DAGs built from
small allocated nodes — impose several costs that conflict with Fabrica’s goals (deterministic simulation, minimal cross-core
traffic, portability toward Rust/C++):

- **Pointer chasing and cache locality**: Nodes scatter across the heap; traversals miss cache lines and prefetch poorly
  compared to dense, predictable layouts.
- **GC or allocator pressure**: Persistent updates allocate new nodes and drop old ones frequently; on managed runtimes this
  stresses the GC; in manual environments it fragments and stresses malloc.
- **Cross-core cache line bouncing**: Shared **atomic reference counts** on hot objects force MESI invalidations across cores
  when many workers touch the same logical refcount word — serializing parallel work that should be independent.
- **Portability**: Designs that rely on tracing GC, finalizers, or pervasive `Arc`/atomics map poorly to Rust/C++ ownership
  and explicit lifetime discipline.

The arena-backed design attacks these together: **dense storage**, **integer handles** instead of pointers, **struct-of-arrays**
separation for refcount vs. payload, **deferred single-threaded refcounting**, and **slab-style O(1) lookup**.

---

## 2. Arena Allocation: Generational Arenas and Slot Maps

### 2.1 Rust ecosystem: `slotmap`, `generational-arena`, `thunderdome`

Three widely used crates illustrate the same core idea:

| Crate | Role | Notes |
|-------|------|--------|
| [**slotmap**](https://docs.rs/slotmap/latest/slotmap/) | Stable keys with versioning | Keys are `(index, version)`; reuse of storage does not revive old keys |
| [**generational-arena**](https://docs.rs/generational-arena/latest/generational_arena/) | Safe arena with deletion | Explicit generational indices to avoid ABA when slots are reused |
| [**thunderdome**](https://docs.rs/thunderdome/latest/thunderdome/) | Small 8-byte keys | Compared against slotmap and generational-arena; same generational pattern |

**Mechanism**: Each slot stores a **generation** (monotonic per slot, incremented on reuse). A **handle** is `(index,
generation)`. Lookup succeeds only if the stored generation matches the handle. When index `i` is freed and reallocated, the
generation at `i` advances, so stale handles fail safely instead of reading the wrong object — the classic **ABA** hazard for
naïve index recycling is avoided.

### 2.2 Complexity and wrap-around

- **Insert / access / remove**: Typically **O(1)** amortized (dense backing `Vec`-like storage, free list or tombstones for
  reuse).
- **Version wrap-around**: `slotmap` documents that after **2³¹** deletions and insertions **to the same underlying slot**, the
  version can wrap and a stale key could theoretically match again — deemed astronomically unlikely in practice; behavior
  remains memory-safe ([slotmap docs — performance](https://docs.rs/slotmap/latest/slotmap/)).

### 2.3 Fabrica-specific insight: generations as a debug safety net

For **persistent structures with correct reference counting** (acyclic graphs, no dangling intentional references), **ABA at
an index should be impossible in correct code**: a live handle implies a positive refcount; the slot cannot be recycled until
all handles are gone. Generations then function as a **belt-and-suspenders** check: they detect **refcount bugs** (use-after-free, double-free) that
would otherwise manifest as silent corruption.

**Design consequence**: In Fabrica’s arena design, **generation fields can be debug-only overhead** in release builds: they
pay for diagnostics and stale-handle detection without affecting the steady-state refcounting model. Production paths can omit
or assert-check generations only in `DEBUG`, matching the project’s preference for explicit lifetimes and correctness
guards.

---

## 3. ECS Struct-of-Arrays (SoA)

### 3.1 Industry pattern

**Entity-Component-System** engines store **components in separate packed arrays**, keyed by **entity ID**, rather than storing
objects as heap structs with pointers:

- **Unity DOTS / Entities**: Components live in chunks/archetypes; iteration over one component type touches dense arrays
  ([Unity ECS concepts](https://docs.unity3d.com/Packages/com.unity.entities@latest)).
- **Bevy**: Components in `Table`/`Blob` storage; **Entity** is a compact ID ([Bevy ECS — Entity](https://docs.rs/bevy_ecs/latest/bevy_ecs/entity/struct.Entity.html)).
- **EnTT**: Sparse sets map entity IDs to packed component arrays ([EnTT wiki — Entity-component-system](https://github.com/skypjack/entt/wiki/Entity-Component-System)).

### 3.2 Cache behavior

**Struct-of-arrays** means a system that only reads component `A` pulls **only `A`’s bytes** into cache lines during the scan.
If `A` and heavy metadata (e.g. refcount or debug flags) lived in the same struct, every pass would drag irrelevant words
through the hierarchy.

### 3.3 SoA split for Fabrica: nodes vs. refcounts

Separating **node payload** (persistent node `struct`s in the arena) from **refcount storage** (parallel `int[]` tables or
slabs) yields:

- **Coordinator refcount passes** iterate only refcount arrays — they do **not** evict node data from caches needed by workers
  for structural edits.
- **Worker hot paths** that read/write node fields avoid touching refcount cache lines.

This mirrors ECS “one aspect per array,” specialized for **lifetime metadata** vs. **domain data**.

### 3.4 Bevy entity IDs: index + generation

Bevy’s `Entity` combines index and generation (packed into a `u64`), the same **stable handle** idea as generational arenas
([Entity](https://docs.rs/bevy_ecs/latest/bevy_ecs/entity/struct.Entity.html)). Fabrica’s handle story aligns with this
ecosystem norm: small, copyable, serializable IDs instead of pointers.

---

## 4. Deferred Reference Counting

### 4.1 Classic deferred RC

**Deutsch & Bobrow (1976)** describe incremental garbage collection with **deferred** work: log pointer operations and reconcile
in batches, using structures like a **zero count table** tied to when stack/register references are known
([PDF — CACM 1976](https://people.cs.umass.edu/~emery/classes/cmpsci691s-fall2004/papers/p522-deutsch.pdf)).

The general theme — **batch or relocate refcount mutations** away from the hottest paths — recurs in modern systems.

### 4.2 Swift: biased reference counting (PACT 2018)

**Biased Reference Counting** splits counts: an **owner thread** uses a **non-atomic** fast path on a biased counter; other
threads use atomics on a shared portion when needed. Large real-world speedups when most objects are thread-local
([Choi, Shull, Torrellas — PACT 2018 PDF](https://iacoma.cs.uiuc.edu/iacoma-papers/pact18.pdf)).

**Relevance**: Shows that **removing atomics from the common case** is a primary optimization target for refcount-heavy code.

### 4.3 Linux `percpu_ref`

**Per-CPU reference counts** maintain sub-counts per CPU; steady-state updates stay local; a **phase transition** (e.g. teardown)
switches to a global view when needed ([Corbet — *Per-CPU reference counts*, LWN 2015](https://lwn.net/Articles/557478/)).

**Relevance**: **Fast local updates**, **expensive coordination only at lifecycle boundaries** — analogous to Fabrica’s
tick-scoped merge.

### 4.4 Concurrent Deferred RC (CDRC), PLDI 2021

**Concurrent Deferred Reference Counting** (Anderson et al., PLDI 2021) achieves constant-time overhead characteristics for
deferred RC in concurrent settings ([ACM DL](https://dl.acm.org/doi/10.1145/3453483.3454060),
[code](https://github.com/cmuparlay/concurrent_deferred_rc)).

**Relevance**: Academic grounding for **deferring decrements** until safe phases — complementary to Fabrica’s simpler **single-
owner coordinator** model (no concurrent RC mutation).

### 4.5 Fabrica’s approach (summary)

- **Per-thread SPSC logs** of refcount **increment** and **decrement** events (or higher-level “acquire/release handle” events).
- **One coordinator thread** drains logs at a **fork–join boundary** and applies refcount updates **without atomics** on shared
  counters.
- **Recursive / cascade free**: use an **explicit worklist** (queue) instead of deep recursion when a node’s refcount hits
  zero and children must be decremented — bounds **stack depth** and allows **per-tick work caps** (incremental reclamation).
- **Cycles**: **Not applicable** for acyclic persistent structures (trees, DAGs **without** parent pointers forming cycles).
  If the representation guarantees no cycles, refcount reclamation is exact without cycle collection.

See **`deferred-reference-counting.md`** in this directory for the broader survey (epoch reclamation, hazard pointers, etc.).

---

## 5. Slab Allocation with O(1) Lookup

### 5.1 Two-level directory

Model storage as:

```text
directory[slab_id][offset]   // each entry is a contiguous slab of 2^k elements
```

- **`slab_id`**: high bits of a global index — selects a slab row.
- **`offset`**: low **`k`** bits — index within the slab.

With **power-of-2 slab size**:

```text
slab_id = global_index >> k
offset  = global_index & ((1 << k) - 1)
```

Only **bit shifts and masks** — no integer division.

### 5.2 Pre-sized directory (Fabrica plan)

A **pre-allocated directory** of **65,536** slab pointers (~512 KB for 64-bit pointers) **never grows** — eliminating resize
races and amortized copy costs at scale. Combined with **~1024 nodes/slab** (for 64-byte nodes under LOH-aware sizing), **order
~67M nodes** fits within a practical upper bound (~4 GB of node payload alone — a stated ceiling in the design plan).

### 5.3 Comparison of lookup structures

| Structure | Lookup | Drawback for arena |
|-----------|--------|---------------------|
| Flat `T[]` | O(1) | **Huge contiguous reservation** or **expensive resize**; LOH issues for large `T[]` |
| B-tree / trie by index | O(log N) | Unnecessary overhead for random access by integer |
| **Two-level slab + directory** | O(1) two loads | Fixed directory; slabs allocated on demand; aligns with **paged** growth |

Fabrica chooses the **two-level** scheme for O(1) access with **bounded directory cost** and **LOH-friendly** slab arrays.

### 5.4 LOH-aware slab sizing (.NET)

`ProducerConsumerQueue<T>.SlabSizeHelper` mirrors Roslyn-style **segment sizing**: choose the largest **power-of-2** element
count such that the backing array stays **below the Large Object Heap threshold** (~85,000 bytes), yielding **`SlabShift`** and
**`OffsetMask`** for bitwise addressing
(`src/Fabrica.Core/Collections/ProducerConsumerQueue.SlabSizeHelper.cs`).

The same sizing discipline applies to arena slabs: **keep each slab’s `T[]` out of the LOH** where possible, improving GC
behavior on managed runtimes and matching fixed slab geometry for bit-extract indexing.

---

## 6. Thread-Local Allocation with Deferred Merge

### 6.1 Pattern

1. **Workers** allocate new nodes into **thread-private append-only buffers** — **no synchronization** during the parallel phase.
2. References among nodes created in the same buffer use **local indices**; references to existing global data use **global
   indices**.
3. A **tag bit** (e.g. high bit of a 32-bit index) distinguishes **local vs. global** references so fixup knows which remap
   table to use.
4. At the merge barrier, the **coordinator** assigns **global indices** (free list or bump), builds **local → global** maps,
   **copies** structs into the arena, and runs **`FixupReferences`** to rewrite tagged fields.

This is the same **parallel collect → merge** shape as:

- **Histogram → prefix sum → scatter** (parallel radix sort / partition),
- **Per-thread bags merged at a barrier** in fork–join runtimes.

### 6.2 Bevy analogue: `ParallelCommands` / `apply_buffers`

Bevy schedules command buffers from parallel systems and **applies** them on the main thread when safe — merging structural
changes after parallel reads ([Bevy 0.5 — *Parallel Commands*](https://bevyengine.org/news/bevy-0-5/#parallel-commands)). Fabrica’s
**coordinator merge** is conceptually similar: **parallel producers** record edits; **single-threaded phase** commits them into
global storage with consistent IDs.

---

## 7. Handle-Based Object Systems

Commercial engines favor **small integer handles** over raw pointers for stability, serialization, and tooling:

- **id Tech / general engine practice**: opaque IDs into tables or arenas; pointers are internal implementation details.
- **Unreal Engine**: `TWeakObjectPtr` / object items use **index + serial number** for stale detection
  ([Unreal — `TWeakObjectPtr`](https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Runtime/CoreUObject/TWeakObjectPtr)).
- **Bevy**: `Entity` as **index + generation** (see §3.4).

**Properties valuable for Fabrica**:

- Handles are **4–8 bytes**, **blittable**, **serializable**, and map cleanly to **Rust/C++** (IDs into `Vec`/`arena`).
- **Stale detection** via generation or serial aligns with **generational arenas** (§2).

---

## 8. Existing Fabrica Patterns Leveraged

These in-repo components inform or mirror the arena design:

| Pattern | Location / idea |
|---------|-----------------|
| **Slab chains, free-stack recycling, LOH-aware sizing** | `ProducerConsumerQueue<T>` — slabs as segments; producer-side **LIFO `_freeSlabs`**
  stack; `SlabSizeHelper` (`ProducerConsumerQueue.cs`, `ProducerConsumerQueue.SlabSizeHelper.cs`, `ProducerConsumerQueue.Slab.cs`) |
| **`ICleanupHandler`** | Struct-constrained callback; **JIT specialization**, no virtual dispatch on hot cleanup
  (`ProducerConsumerQueue.ICleanupHandler.cs`) |
| **`IAllocator<T>`** | Pool allocator interface; used with **`ObjectPool<T, TAllocator>`** (`IAllocator.cs`) |
| **`ObjectPool<T, TAllocator>`** | Single-threaded **LIFO** stack; **DEBUG** thread-owner assertion; **`TAllocator : struct`**
  eliminates interface dispatch on `Allocate`/`Reset` (`ObjectPool.cs`) |
| **`WorkStealingDeque<T>`** | Ring buffer **growth** leaves old buffers for GC after thieves finish — **GC reliance
  documented** in type docs; arena design avoids this for node storage by using explicit pooling/slabs
  (`WorkStealingDeque.cs`) |

---

## 9. Recommendations (Research → Engineering)

Consolidated guidance for implementers (see **`plans/2026-04-03/arena-persistent-structures.md`** for the concrete API sketch):

1. **Pre-allocated fixed-size directory** (no growth); **power-of-2** slabs sized with **LOH-aware** limits (same philosophy as
   `SlabSizeHelper`).
2. **Struct-of-arrays**: separate **node payload** arrays from **refcount** arrays so refcount passes do not share cache lines
   with node data iteration.
3. **Single-threaded coordinator** for all refcount mutations → **zero atomics** on refcounts in steady state.
4. **Per-thread append buffers** with a **tag bit** distinguishing local vs. global indices during parallel allocation.
5. **LIFO free list** for recycled indices — **cache-hot reuse** (same rationale as `ProducerConsumerQueue` slab stack and
   `ObjectPool` LIFO).
6. **Debug-only generational** (or serial) fields in handles for **stale reference** detection; optional in release.
7. **Worklist-based cascade free** to bound **recursion depth** and optionally **per-frame** reclamation cost.

---

## References (URLs)

| Topic | Link |
|-------|------|
| slotmap | https://docs.rs/slotmap/latest/slotmap/ |
| generational-arena | https://docs.rs/generational-arena/latest/generational_arena/ |
| thunderdome | https://docs.rs/thunderdome/latest/thunderdome/ |
| Bevy Entity | https://docs.rs/bevy_ecs/latest/bevy_ecs/entity/struct.Entity.html |
| Bevy 0.5 parallel commands | https://bevyengine.org/news/bevy-0-5/#parallel-commands |
| Unity ECS package docs | https://docs.unity3d.com/Packages/com.unity.entities@latest |
| EnTT ECS wiki | https://github.com/skypjack/entt/wiki/Entity-Component-System |
| Deutsch & Bobrow 1976 (PDF) | https://people.cs.umass.edu/~emery/classes/cmpsci691s-fall2004/papers/p522-deutsch.pdf |
| Swift Biased RC (PACT 2018 PDF) | https://iacoma.cs.uiuc.edu/iacoma-papers/pact18.pdf |
| Linux percpu_ref (LWN) | https://lwn.net/Articles/557478/ |
| CDRC PLDI 2021 (ACM) | https://dl.acm.org/doi/10.1145/3453483.3454060 |
| CDRC code | https://github.com/cmuparlay/concurrent_deferred_rc |
| Unreal `TWeakObjectPtr` | https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Runtime/CoreUObject/TWeakObjectPtr |

---

*End of document.*
