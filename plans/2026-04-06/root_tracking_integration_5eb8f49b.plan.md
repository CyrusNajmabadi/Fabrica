---
name: Root Tracking Integration
overview: Add root tracking to ThreadLocalBuffer<T> using UnsafeList<Handle<T>>, with both MarkRoot and an Allocate(bool isRoot) overload. Update the coordinator merge tests to collect, remap, and increment roots from TLBs instead of using hardcoded root arrays.
todos:
  - id: tlb-root-tracking
    content: "Add UnsafeList<Handle<T>> _roots to ThreadLocalBuffer<T>: MarkRoot(Handle<T>), Allocate(bool isRoot) overload, RootHandles span, Reset clears roots"
    status: completed
  - id: tlb-root-tests
    content: "Add root tracking tests to ThreadLocalBufferTests: MarkRoot, Allocate(isRoot: true), RootHandles, Reset clears roots, cross-TLB root"
    status: completed
  - id: update-merge-tests
    content: "Update CoordinatorMergeTests: add root remap helper, replace hardcoded root arrays with TLB-driven root collection + remap in both end-to-end tests"
    status: completed
  - id: root-invariant-test
    content: "Add test asserting post-Phase2b invariant: roots have RC=0 before IncrementRoots, RC>0 after"
    status: completed
  - id: update-todo-md
    content: Mark root tracking TODO item as done in TODO.md
    status: completed
isProject: false
---

# Root Tracking Integration

## Design

Roots are stored as `UnsafeList<Handle<T>>` inside `ThreadLocalBuffer<T>`. A root handle can reference a node from **any** TLB (not just the owning one), so the list stores full `Handle<T>` values rather than bare local indices.

### ThreadLocalBuffer changes (`[src/Fabrica.Core/Memory/ThreadLocalBuffer.cs](src/Fabrica.Core/Memory/ThreadLocalBuffer.cs)`)

- Add field: `private readonly UnsafeList<Handle<T>> _roots = new(initialCapacity: 64);`
- Add `MarkRoot(Handle<T> handle)` — appends handle to root list (no validation needed; handle may be local to a different thread)
- Add `Allocate(bool isRoot)` overload — allocates a slot, and if `isRoot` is true, also calls `MarkRoot` with the returned handle
- Existing `Allocate()` forwards to `Allocate(isRoot: false)`
- Add `RootHandles` property returning `ReadOnlySpan<Handle<T>>` over `_roots`
- `Reset()` clears both `_list` and `_roots`

### Root remapping during merge

During the coordinator merge, root handles stored in TLBs are local handles that need remapping to global indices, just like child references inside nodes. The test helpers will:

1. After Phase 2a (fixup), collect roots from all TLBs for each type
2. Remap each root handle: if local, resolve via the appropriate `RemapTable`; if already global, leave as-is
3. Feed the remapped roots into `NodeStore.IncrementRoots`

This uses the same `TaggedHandle.IsLocal` / `DecodeThreadId` / `DecodeLocalIndex` + `RemapTable.Resolve` logic already used by `RemapVisitor`, but applied to loose handles rather than node fields.

### Test updates

- `**ThreadLocalBufferTests`**: Add tests for `MarkRoot`, `Allocate(isRoot: true)`, `RootHandles` span, `Reset` clears roots, cross-TLB root marking
- `**CoordinatorMergeTests**`: Replace hardcoded `Handle<ParentNode>[] roots = [new(0), new(2)]` with TLB-driven root collection + remap in both end-to-end tests. Add a new test asserting the post-Phase2b invariant (roots have RC=0 before increment, RC>0 after)
- `**TODO.md**`: Mark the root tracking item as done

