# Source Generator: Immutable Nodes with Mutable Sibling Types

## Problem

Game node structs (e.g. `MachineNode`, `BeltSegmentNode`) need to be **readonly** at the game
level — mutating a node that lives in the global arena would violate the persistent/immutable
snapshot model. But the merge pipeline must **rewrite handle fields in place** when promoting
local TLB nodes to global arena slots (replacing tagged local handles with global indices).

Today the node structs are fully mutable, which means game code could accidentally mutate
a node it read from a snapshot.

## Design

The user defines nodes as `readonly partial struct`:

```csharp
public readonly partial struct MachineNode
{
    public Handle<BeltSegmentNode> InputBelt { get; init; }
    public Handle<BeltSegmentNode> OutputBelt { get; init; }
    public int RecipeId { get; init; }
    public int Progress { get; init; }
}
```

The source generator:

1. **Validates** that the user-defined part is `readonly`. Emits a diagnostic if not.

2. **Emits the other partial part** adding `[StructLayout(LayoutKind.Sequential)]` (the user
   doesn't need to remember this).

3. **Emits a mutable sibling type** with identical field layout in a separate namespace
   (e.g. `Fabrica.Core.Memory.Mutable` or similar) to keep it mostly hidden from game code:

   ```csharp
   [StructLayout(LayoutKind.Sequential)]
   internal struct MutableMachineNode
   {
       public Handle<BeltSegmentNode> InputBelt;
       public Handle<BeltSegmentNode> OutputBelt;
       public int RecipeId;
       public int Progress;
   }
   ```

4. Because both types share `LayoutKind.Sequential` and identical field order/types, the
   merge pipeline can `Unsafe.As<MachineNode, MutableMachineNode>(ref node)` to get a
   writable reference for handle rewriting — zero-cost, no copy.

## Invariants

- Game code only ever sees `readonly MachineNode`. It cannot mutate fields.
- The mutable sibling is `internal` to the core/generated layer. Game code never touches it.
- `EnumerateRefChildren` (which rewrites handles) operates on `ref MutableMachineNode` via
  `Unsafe.As`. The visitor's `VisitRef<T>(ref Handle<T>)` writes through the mutable ref.
- `EnumerateChildren` (read-only path for cascade, refcount) uses `in MachineNode` directly.

## Benefits

- Compile-time enforcement: game code cannot accidentally mutate snapshot nodes.
- Zero runtime cost: `Unsafe.As` between layout-compatible structs is a no-op.
- The `LayoutKind.Sequential` attribute is auto-emitted — one less thing to forget.
- The mutable sibling is generated and hidden — no manual boilerplate.
