---
name: Simplify node generics
overview: Simplify the generics story by dropping unused context variants, merging INodeChildEnumerator into INodeOps (inheriting INodeVisitor), and reducing per-type boilerplate from 8 DIM methods to 4.
todos:
  - id: rename-interface
    content: "Rename INodeChildEnumerator<TNode> to INodeOps<TNode> : INodeVisitor, drop context overloads, rename file"
    status: completed
  - id: delete-context-visitor
    content: Delete INodeVisitor<TContext> from INodeVisitor.cs
    status: completed
  - id: update-constraints
    content: Update NodeStore, DagValidator, SnapshotSlice constraints to INodeOps<TNode>
    status: completed
  - id: update-impls
    content: Update all *NodeOps structs and enumerator impls to implement INodeOps<TNode> instead of two interfaces
    status: completed
  - id: update-benchmarks
    content: Remove context overloads from benchmarks and JIT baseline, regenerate baselines if needed
    status: completed
  - id: verify
    content: Build, format, test (Debug + Release)
    status: completed
isProject: false
---

# Simplify Node Interface Generics

## Current state (confusing)

Three interfaces, 8 total DIM methods, two generics strategies for TContext:

- `INodeVisitor` — 2 methods (Visit, VisitRef)
- `INodeVisitor<TContext>` — 2 methods (Visit+context, VisitRef+context), TContext is **type-level**
- `INodeChildEnumerator<TNode>` — 4 methods (Enumerate/EnumerateRef, with/without context), TContext is **method-level**

NodeStore constraint: `TNodeOps : struct, INodeChildEnumerator<TNode>, INodeVisitor` (two interfaces)

## Proposed state (simple)

Two interfaces, 4 total DIM methods, no context variants:

- `INodeVisitor` — 2 methods: `Visit<TChild>`, `VisitRef<TChild>` (unchanged)
- `INodeOps<TNode> : INodeVisitor` — 2 methods: `EnumerateChildren<TVisitor>`, `EnumerateRefChildren<TVisitor>` where `TVisitor : struct, INodeVisitor`

NodeStore constraint: `TNodeOps : struct, INodeOps<TNode>` (one interface, implies INodeVisitor)

## Why INodeVisitor stays separate

The `EnumerateChildren` method constrains its `TVisitor` to `INodeVisitor`. A visitor (e.g., increment refcounts) handles `Handle<TChild>` for arbitrary child types — it does not need to know the parent's `TNode`. So the visitor interface cannot carry a `TNode` type parameter. `INodeVisitor` must exist as a standalone base that `INodeOps<TNode>` inherits.

## Why context variants are dropped

`INodeVisitor<TContext>` and the context overloads of `EnumerateChildren` are **never called in production or test code**. Every real visitor captures its context at construction time. These methods only appear as dead implementations in benchmarks/JIT baselines. If context is needed in the future, the source generator can add targeted support.

## Changes

### Source files

- [src/Fabrica.Core/Memory/INodeVisitor.cs](src/Fabrica.Core/Memory/INodeVisitor.cs) — Delete `INodeVisitor<TContext>` entirely. `INodeVisitor` stays with its 2 methods.
- [src/Fabrica.Core/Memory/INodeChildEnumerator.cs](src/Fabrica.Core/Memory/INodeChildEnumerator.cs) — Rename to `INodeOps.cs`. Rename interface to `INodeOps<TNode> : INodeVisitor`. Remove the 2 context overloads. Keep `EnumerateChildren<TVisitor>` and `EnumerateRefChildren<TVisitor>` (2 methods).
- [src/Fabrica.Core/Memory/NodeStore.cs](src/Fabrica.Core/Memory/NodeStore.cs) — Change `TNodeOps : struct, INodeChildEnumerator<TNode>, INodeVisitor` to `TNodeOps : struct, INodeOps<TNode>`.
- [src/Fabrica.Core/Memory/DagValidator.cs](src/Fabrica.Core/Memory/DagValidator.cs) — Update constraint from `INodeChildEnumerator<TNode>, INodeVisitor` to `INodeOps<TNode>`.

### Test files

- All `*NodeOps` structs: change `INodeChildEnumerator<T>, INodeVisitor` to `INodeOps<T>`. Remove any context overload implementations.

### Benchmarks / JIT baseline

- Remove context overload implementations from enumerator structs in benchmarks and [tests/Fabrica.JitBaseline/Program.cs](tests/Fabrica.JitBaseline/Program.cs).
- JIT baselines may need regeneration if the simpler interface changes codegen.
