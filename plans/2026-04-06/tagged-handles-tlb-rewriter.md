# Tagged Handles, ThreadLocalBuffer, and INodeHandleRewriter

## What

Three building blocks for the parallel work phase and coordinator merge:

1. **TaggedHandle** — Static encoding/decoding helpers for the tagged handle bit layout.
2. **ThreadLocalBuffer<T>** — Per-worker, per-type append-only buffer for node creation.
3. **INodeHandleRewriter** — Visitor extension for in-place handle rewriting during fixup.

## Bit Layout

```
Global (bit 31 = 0): [0][31 bits: global arena index]  — range 0..2B
Local  (bit 31 = 1): [1][7 bits: thread ID][24 bits: local index]
None   (-1 / 0xFFFFFFFF): sentinel — neither global nor local
```

Key: `Handle<T>.IsValid` returns `Index >= 0`, which is only true for global handles. Local
handles are negative (bit 31 set) but not -1. The `RewriteChildren` method guards with
`Index != -1` (not None) instead of `IsValid` so both global and local handles are rewritten.

## ThreadLocalBuffer

Backed by `UnsafeList<T>`. Workers call `Allocate()` to get a local handle with their thread ID
baked in. The coordinator reads `WrittenSpan` after the join barrier to drain and merge.

## INodeHandleRewriter + RewriteChildren

New method on `INodeChildEnumerator<TNode>`:

```csharp
void RewriteChildren<TRewriter>(ref TNode node, ref TRewriter rewriter)
    where TRewriter : struct, INodeHandleRewriter;
```

Takes `ref TNode` (not `in`) for in-place mutation. Uses by-value pattern for property handles:
`var h = node.Prop; rewriter.Rewrite(ref h); node.Prop = h;`.

## Files

| File | Change |
|------|--------|
| `src/Fabrica.Core/Memory/TaggedHandle.cs` | New |
| `src/Fabrica.Core/Memory/ThreadLocalBuffer.cs` | New |
| `src/Fabrica.Core/Memory/INodeHandleRewriter.cs` | New |
| `src/Fabrica.Core/Memory/INodeChildEnumerator.cs` | Added `RewriteChildren` |
| `tests/Fabrica.Core.Tests/Memory/TaggedHandleTests.cs` | New |
| `tests/Fabrica.Core.Tests/Memory/ThreadLocalBufferTests.cs` | New |
| `tests/Fabrica.Core.Tests/Memory/HandleRewriterTests.cs` | New |
| 13 existing enumerator implementations | Added `RewriteChildren` |
