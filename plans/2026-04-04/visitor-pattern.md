# Generic Visitor Pattern for DAG Traversal

## Status: Planned (not yet implemented)

## Problem

We've written a lot of logic for walking DAGs — needed for:

1. **Validation** (`DagValidator`) — enumerate children, check acyclicity, refcounts, reachability
2. **Refcount increment** — walk new nodes, increment children's refcounts
3. **Cascade-free decrement** — when a node hits refcount zero, walk its children and decrement them

These all need to enumerate children of a node and process them. When DAGs only contain nodes of the same type, this is straightforward. But because nodes can have child references to other data types (cross-type DAGs: `ParentNode.ChildRef → ChildNode`), this gets more complex.

## Proposed Design

### Struct-Callback Visitor

Use the existing struct generic callback pattern. The callback receives the **full snapshot** (or a "world context") as an argument (probably as `in`). Each node type's visitor implementation then recurses into its children, passing:

- The correct child `Handle<T>` for each child
- The correct `SnapshotSlice<T, THandler>` (selected from the full snapshot) for that child's type

This is a **strongly-typed struct-visitor approach**. Recursing downward is a function of the particular child-iterator callback provided, but a no-op callback would simply see each child node.

### Context Type

The "context" passed to the visitor needs to provide access to all the `SnapshotSlice` instances in the snapshot, so the visitor can select the right one for each child type. This is the "full snapshot" or "store registry" that the visitor receives.

### Unifying Traversal

The visitor pattern should unify:

- `DagValidator.IChildEnumerator<TNode>` (currently only enumerates same-store children)
- `RefCountTable<T>.IRefCountHandler.OnFreed` (currently handles cross-type cascade manually)
- Future increment/walk logic in the coordinator

Into a single generic walker that can handle both same-type and cross-type children uniformly.

## Prerequisites

- **`Handle<T>` typed wrapper** (PR #111) — completed. Makes the visitor type-safe: each child handle carries its type, preventing accidental lookup in the wrong store.
- **Zero-overhead experiment** — completed. Confirmed that struct-constrained generic interface methods (`IChildAction.OnChild<TChild, TChildHandler>`) are fully devirtualized and specialized by the JIT. See `plans/2026-04-04/visitor-pattern-experiment.md` and `benchmarks/results/2026-04-04/visitor-experiment/comparison.md`.

## Open Questions

1. How exactly should the "world context" / "store registry" be structured? A user-composed struct containing all `NodeStore` references? Or a more formal registry?
2. Should the visitor be a single `IVisitor<TNode, TContext>` interface, or split into `IChildEnumerator` + `IChildProcessor`?
3. How does this interact with the coordinator's merge/fixup logic when `ThreadLocalBuffer` work is integrated?

## Non-Goals (for this plan)

- ThreadLocalBuffer / ArenaCoordinator integration
- PCQ integration
- Actual snapshot composition patterns beyond the type definitions
