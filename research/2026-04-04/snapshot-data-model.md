# Snapshot Data Model — Design Rationale

## Problem

Fabrica's arena-backed persistent data structures (`UnsafeSlabArena<T>` + `RefCountTable`) provide
the storage and lifecycle primitives, but there is no formalization of what a "snapshot" of
application state actually is. Without a clear model:

- Root tracking is ad-hoc (callers manually increment/decrement root refcounts).
- There is no type-safe association between an arena, its refcount table, and the roots it manages.
- Cross-type DAGs (e.g., a mesh node referencing a material node in a different arena) have no
  structural support — the relationship is implicit.
- The lifecycle of a snapshot through a producer-consumer queue is not formalized, making it easy
  to forget an increment or decrement and silently corrupt refcounts.

## Design Goals

1. **Type-safe per-type bundles.** Each node type `T` has exactly one arena + refcount table +
   cascade handler. These three should be bundled so they can't be accidentally mixed.
2. **Self-contained snapshot slices.** A snapshot's per-type root set should be able to increment
   and decrement its own root refcounts without external help — no handler parameters needed at the
   call site.
3. **Heterogeneous snapshots.** A snapshot contains slices for multiple node types. The composition
   should be explicit (user-defined struct with typed fields) to avoid boxing and interface dispatch.
4. **PCQ lifecycle alignment.** Increment roots on publish, decrement roots on release. The API
   should make it hard to forget either operation.
5. **Zero allocation in steady state.** Root index lists are reusable. No per-snapshot heap
   allocation after warmup.
6. **Portability.** All types are value-oriented. In Rust: `NodeStore` → `struct` with arena +
   refcounts + handler fn pointer. `SnapshotSlice` → `struct` with `Vec<i32>` + store reference.

## Key Insight: Separation of Concerns

The refcount of a node reflects **two distinct kinds of references**:

1. **Structural references** — parent-to-child edges within the DAG. Established when nodes are
   created (the "walk new nodes, increment children" step).
2. **Root references** — external holds by snapshots. Established when a snapshot is published,
   released when the snapshot is retired.

These are independent. A node with 2 parent edges and held as a root by 3 snapshots has refcount 5.
When the last snapshot releases it and no parent points at it, it cascades to zero.

This separation means:
- New nodes start at refcount 0.
- The coordinator walks new nodes and increments children → establishes structural refcounts.
- `IncrementRootRefCounts` adds the snapshot's hold → establishes root refcounts.
- `DecrementRootRefCounts` removes the snapshot's hold → may trigger cascade-free.

## Design: `NodeStore<TNode, THandler>`

Bundles the three things needed to fully manage a node type's lifecycle:

```csharp
internal sealed class NodeStore<TNode, THandler>
    where TNode : struct, IArenaNode
    where THandler : struct, RefCountTable.IRefCountHandler
{
    public UnsafeSlabArena<TNode> Arena { get; }
    public RefCountTable RefCounts { get; }

    public void IncrementRoots(ReadOnlySpan<int> roots);
    public void DecrementRoots(ReadOnlySpan<int> roots);
}
```

The `THandler` type parameter captures the cascade-free handler for this node type. The handler is
a struct implementing `RefCountTable.IRefCountHandler`, enabling JIT specialization. The store holds
a `THandler` instance so `DecrementRoots` can call `DecrementBatch` without the caller providing the
handler.

**Two type parameters.** The cost is verbosity (`NodeStore<MeshNode, MeshHandler>` everywhere). The
benefit is zero boxing, zero interface dispatch on the cascade hot path. For cross-type DAGs, the
handler captures references to other stores (e.g., `MeshHandler` holds a `NodeStore` reference for
materials so it can decrement material children when a mesh node is freed).

## Design: `SnapshotSlice<TNode, THandler>`

Per-snapshot, per-type root set:

```csharp
internal struct SnapshotSlice<TNode, THandler>
    where TNode : struct, IArenaNode
    where THandler : struct, RefCountTable.IRefCountHandler
{
    private readonly NodeStore<TNode, THandler> _store;
    private List<int> _rootIndices;

    public void AddRoot(int globalIndex);
    public ReadOnlySpan<int> Roots { get; }
    public void Clear();

    public void IncrementRootRefCounts();
    public void DecrementRootRefCounts();
}
```

**Self-contained operations.** `IncrementRootRefCounts` and `DecrementRootRefCounts` need no
arguments — the slice has a reference to its store, which has the handler. This makes it impossible
to pass the wrong handler or forget to pass one.

**`List<int>` for root indices.** One heap object per slice. Grows to steady state and reuses via
`Clear()`. The list is poolable: once a snapshot is fully released, its slices' lists can be reused
by future snapshots.

## Snapshot Composition

A snapshot is a user-defined struct composed of typed slices:

```csharp
struct WorldSnapshot
{
    public SnapshotSlice<MeshNode, MeshHandler> Meshes;
    public SnapshotSlice<TransformNode, TransformHandler> Transforms;

    public void IncrementRootRefCounts() { ... }
    public void DecrementRootRefCounts() { ... }
}
```

This is explicit and zero-overhead. Adding a new node type means adding a field and two lines.
A homogeneous array approach (using a non-generic interface) is possible later if needed, but
the per-type count is small (likely < 10) so explicit composition is fine.

## Cross-Type DAGs

A mesh node can store an `int` field that indexes into the material arena. When the mesh node's
refcount hits zero, its handler (`MeshHandler.OnFreed`) reads the mesh struct, finds the material
child index, and calls `materialStore.RefCounts.Decrement(materialIndex, materialHandler)`. This
works because `RefCountTable` supports re-entrant cascades across tables.

The `NodeStore` for each type is independent — different arenas, different refcount tables. The
handler is the bridge: it captures references to whatever other stores it needs for cross-type
decrements.

## Lifecycle Through PCQ

```
Producer creates new nodes (refcount 0 for all)
  → Walk new nodes, increment children's refcounts (structural references)
  → Assemble snapshot root set
  → IncrementRootRefCounts (root references; roots now at refcount 1)
  → Enqueue snapshot into PCQ

Consumer dequeues snapshot
  → Process it
  → DecrementRootRefCounts (cascade-free any nodes that hit zero)
```

Multiple snapshots sharing structure: a node pointed at by 3 snapshots as a root has refcount 3
(plus any structural references from parents). Releasing one snapshot drops it to 2. Only when the
last holder releases does it potentially cascade.

## Open Questions

- **Root demotion.** When a new node references a former root (subsuming it), should the root
  automatically be demoted? This is a coordinator/TLB concern, deferred to the next design phase.
- **Snapshot pooling.** How to efficiently reuse `List<int>` instances across snapshots. Likely a
  simple per-type pool of lists, or the snapshot struct itself is pooled.
