using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Fabrica.Core.Memory;

namespace Fabrica.Core.Benchmarks;

/// <summary>
/// Measures the overhead (if any) of the visitor pattern (struct-constrained generic interface
/// methods) vs. hand-rolled direct code for the two hot paths:
///   1. Incrementing children's refcounts (the "publish snapshot" path)
///   2. Decrement-with-cascade (the "release snapshot" path)
///
/// Each benchmark has a "Direct" variant (what you'd write by hand) and a "Visitor" variant
/// (using <see cref="IChildEnumerator{TNode,TContext}"/> + <see cref="IChildAction"/>).
/// If the JIT properly devirtualizes and specializes the generic interface methods, both
/// variants should produce identical machine code.
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
public class VisitorPatternBenchmarks
{
    // ── Node types ───────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct ParentNode
    {
        public Handle<ParentNode> LeftParent;
        public Handle<ParentNode> RightParent;
        public Handle<ChildNode> ChildRef;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChildNode
    {
        public Handle<ChildNode> Left;
        public Handle<ChildNode> Right;
    }

    // ── World context (passed as in TContext) ─────────────────────────────

    private struct World
    {
        public NodeStore<ParentNode, DirectParentHandler> ParentStore;
        public NodeStore<ChildNode, DirectChildHandler> ChildStore;
    }

    private struct VisitorWorld
    {
        public NodeStore<ParentNode, VisitorParentHandler> ParentStore;
        public NodeStore<ChildNode, VisitorChildHandler> ChildStore;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // DIRECT (hand-rolled) handlers — the baseline
    // ═══════════════════════════════════════════════════════════════════════

    private struct DirectChildHandler(UnsafeSlabArena<ChildNode> arena) : RefCountTable<ChildNode>.IRefCountHandler
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void OnFreed(Handle<ChildNode> handle, RefCountTable<ChildNode> table)
        {
            ref readonly var node = ref arena[handle];
            if (node.Left.IsValid) table.Decrement(node.Left, this);
            if (node.Right.IsValid) table.Decrement(node.Right, this);
            arena.Free(handle);
        }
    }

    private struct DirectParentHandler(
        UnsafeSlabArena<ParentNode> parentArena,
        NodeStore<ChildNode, DirectChildHandler> childStore) : RefCountTable<ParentNode>.IRefCountHandler
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void OnFreed(Handle<ParentNode> handle, RefCountTable<ParentNode> table)
        {
            ref readonly var node = ref parentArena[handle];
            if (node.LeftParent.IsValid) table.Decrement(node.LeftParent, this);
            if (node.RightParent.IsValid) table.Decrement(node.RightParent, this);
            if (node.ChildRef.IsValid) childStore.DecrementRefCount(node.ChildRef);
            parentArena.Free(handle);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // VISITOR-based handlers — the experiment
    // ═══════════════════════════════════════════════════════════════════════

    // ── Actions ──────────────────────────────────────────────────────────

    private struct IncrementAction : IChildAction
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnChild<TChild, TChildHandler>(
            Handle<TChild> child,
            NodeStore<TChild, TChildHandler> store)
            where TChild : struct
            where TChildHandler : struct, RefCountTable<TChild>.IRefCountHandler
        {
            store.IncrementRefCount(child);
        }
    }

    private struct DecrementAction : IChildAction
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnChild<TChild, TChildHandler>(
            Handle<TChild> child,
            NodeStore<TChild, TChildHandler> store)
            where TChild : struct
            where TChildHandler : struct, RefCountTable<TChild>.IRefCountHandler
        {
            store.DecrementRefCount(child);
        }
    }

    // ── Enumerators ──────────────────────────────────────────────────────

    private struct ChildNodeEnumerator : IChildEnumerator<ChildNode, VisitorWorld>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnumerateChildren<TAction>(in ChildNode node, in VisitorWorld context, ref TAction action)
            where TAction : struct, IChildAction
        {
            if (node.Left.IsValid) action.OnChild(node.Left, context.ChildStore);
            if (node.Right.IsValid) action.OnChild(node.Right, context.ChildStore);
        }
    }

    private struct ParentNodeEnumerator : IChildEnumerator<ParentNode, VisitorWorld>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnumerateChildren<TAction>(in ParentNode node, in VisitorWorld context, ref TAction action)
            where TAction : struct, IChildAction
        {
            if (node.LeftParent.IsValid) action.OnChild(node.LeftParent, context.ParentStore);
            if (node.RightParent.IsValid) action.OnChild(node.RightParent, context.ParentStore);
            if (node.ChildRef.IsValid) action.OnChild(node.ChildRef, context.ChildStore);
        }
    }

    // ── Visitor-composed handlers ────────────────────────────────────────

    private struct VisitorChildHandler : RefCountTable<ChildNode>.IRefCountHandler
    {
        public VisitorWorld World;
        public UnsafeSlabArena<ChildNode> Arena;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnFreed(Handle<ChildNode> handle, RefCountTable<ChildNode> table)
        {
            ref readonly var node = ref this.Arena[handle];
            var action = new DecrementAction();
            default(ChildNodeEnumerator).EnumerateChildren(in node, in this.World, ref action);
            this.Arena.Free(handle);
        }
    }

    private struct VisitorParentHandler : RefCountTable<ParentNode>.IRefCountHandler
    {
        public VisitorWorld World;
        public UnsafeSlabArena<ParentNode> Arena;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnFreed(Handle<ParentNode> handle, RefCountTable<ParentNode> table)
        {
            ref readonly var node = ref this.Arena[handle];
            var action = new DecrementAction();
            default(ParentNodeEnumerator).EnumerateChildren(in node, in this.World, ref action);
            this.Arena.Free(handle);
        }
    }

    // ── Parameters ───────────────────────────────────────────────────────

    [Params(1_000, 10_000, 100_000)]
    public int N { get; set; }

    // ── State ────────────────────────────────────────────────────────────

    private World _direct;
    private VisitorWorld _visitor;

    // Pre-allocated handle arrays so benchmark loops don't allocate
    private Handle<ParentNode>[] _parentHandles = null!;
    private Handle<ChildNode>[] _childHandles = null!;

    [IterationSetup]
    public void Setup()
    {
        // ── Direct world ─────────────────────────────────────────────────
        {
            var childArena = new UnsafeSlabArena<ChildNode>();
            var childRC = new RefCountTable<ChildNode>();
            var childHandler = new DirectChildHandler(childArena);
            var childStore = new NodeStore<ChildNode, DirectChildHandler>(childArena, childRC, childHandler);

            var parentArena = new UnsafeSlabArena<ParentNode>();
            var parentRC = new RefCountTable<ParentNode>();
            var parentHandler = new DirectParentHandler(parentArena, childStore);
            var parentStore = new NodeStore<ParentNode, DirectParentHandler>(parentArena, parentRC, parentHandler);

            _direct = new World { ParentStore = parentStore, ChildStore = childStore };
        }

        // ── Visitor world ────────────────────────────────────────────────
        {
            var childArena = new UnsafeSlabArena<ChildNode>();
            var childRC = new RefCountTable<ChildNode>();
            var childHandler = new VisitorChildHandler { Arena = childArena };
            var childStore = new NodeStore<ChildNode, VisitorChildHandler>(childArena, childRC, childHandler);

            var parentArena = new UnsafeSlabArena<ParentNode>();
            var parentRC = new RefCountTable<ParentNode>();
            var parentHandler = new VisitorParentHandler { Arena = parentArena };
            var parentStore = new NodeStore<ParentNode, VisitorParentHandler>(parentArena, parentRC, parentHandler);

            _visitor = new VisitorWorld { ParentStore = parentStore, ChildStore = childStore };

            // Patch the world reference into the handlers via the stores.
            // NodeStore holds the handler as a readonly copy, so we need to rebuild
            // after the world is complete. This is a one-time setup cost.
            childHandler.World = _visitor;
            parentHandler.World = _visitor;
            childStore = new NodeStore<ChildNode, VisitorChildHandler>(childArena, childRC, childHandler);
            parentStore = new NodeStore<ParentNode, VisitorParentHandler>(parentArena, parentRC, parentHandler);
            _visitor = new VisitorWorld { ParentStore = parentStore, ChildStore = childStore };

            // Final patch: handlers inside the stores now hold a VisitorWorld whose stores
            // are the old ones. Rebuild once more to close the loop.
            childHandler.World = _visitor;
            parentHandler.World = _visitor;
            childStore = new NodeStore<ChildNode, VisitorChildHandler>(childArena, childRC, childHandler);
            parentStore = new NodeStore<ParentNode, VisitorParentHandler>(parentArena, parentRC, parentHandler);
            _visitor = new VisitorWorld { ParentStore = parentStore, ChildStore = childStore };
        }

        // ── Allocate nodes in both worlds ────────────────────────────────
        BuildWorld(ref _direct);
        BuildVisitorWorld(ref _visitor);
    }

    private void BuildWorld(ref World world)
    {
        var childArena = world.ChildStore.Arena;
        var childRC = world.ChildStore.RefCounts;
        var parentArena = world.ParentStore.Arena;
        var parentRC = world.ParentStore.RefCounts;

        childRC.EnsureCapacity(this.N * 2);
        parentRC.EnsureCapacity(this.N);

        _childHandles = new Handle<ChildNode>[this.N];
        _parentHandles = new Handle<ParentNode>[this.N];

        // Allocate child nodes (binary tree leaves — no children)
        for (var i = 0; i < this.N; i++)
        {
            var h = childArena.Allocate();
            childArena[h] = new ChildNode { Left = Handle<ChildNode>.None, Right = Handle<ChildNode>.None };
            _childHandles[i] = h;
        }

        // Allocate parent nodes, each pointing at two child nodes
        for (var i = 0; i < this.N; i++)
        {
            var h = parentArena.Allocate();
            var childLeft = _childHandles[i];
            var childRight = _childHandles[(i + 1) % this.N];
            parentArena[h] = new ParentNode
            {
                LeftParent = Handle<ParentNode>.None,
                RightParent = Handle<ParentNode>.None,
                ChildRef = childLeft,
            };
            _parentHandles[i] = h;
        }
    }

    private void BuildVisitorWorld(ref VisitorWorld world)
    {
        var childArena = world.ChildStore.Arena;
        var childRC = world.ChildStore.RefCounts;
        var parentArena = world.ParentStore.Arena;
        var parentRC = world.ParentStore.RefCounts;

        childRC.EnsureCapacity(this.N * 2);
        parentRC.EnsureCapacity(this.N);

        // Allocate child nodes
        for (var i = 0; i < this.N; i++)
        {
            var h = childArena.Allocate();
            childArena[h] = new ChildNode { Left = Handle<ChildNode>.None, Right = Handle<ChildNode>.None };
        }

        // Allocate parent nodes
        for (var i = 0; i < this.N; i++)
        {
            var h = parentArena.Allocate();
            parentArena[h] = new ParentNode
            {
                LeftParent = Handle<ParentNode>.None,
                RightParent = Handle<ParentNode>.None,
                ChildRef = new Handle<ChildNode>(i),
            };
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // INCREMENT BENCHMARKS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Direct: for each parent node, read its fields and call Increment on the appropriate
    /// refcount table for each valid child. This is the hand-rolled baseline.
    /// </summary>
    [Benchmark(Baseline = true)]
    public int IncrementChildren_Direct()
    {
        var parentArena = _direct.ParentStore.Arena;
        var parentRC = _direct.ParentStore.RefCounts;
        var childRC = _direct.ChildStore.RefCounts;
        var count = 0;

        for (var i = 0; i < this.N; i++)
        {
            ref readonly var node = ref parentArena[_parentHandles[i]];
            if (node.LeftParent.IsValid) { parentRC.Increment(node.LeftParent); count++; }
            if (node.RightParent.IsValid) { parentRC.Increment(node.RightParent); count++; }
            if (node.ChildRef.IsValid) { childRC.Increment(node.ChildRef); count++; }
        }

        return count;
    }

    /// <summary>
    /// Visitor: for each parent node, use the enumerator + IncrementAction to increment
    /// children's refcounts. Should produce identical code after JIT specialization.
    /// </summary>
    [Benchmark]
    public int IncrementChildren_Visitor()
    {
        var parentArena = _visitor.ParentStore.Arena;
        var enumerator = default(ParentNodeEnumerator);
        var action = new IncrementAction();
        var count = 0;

        for (var i = 0; i < this.N; i++)
        {
            ref readonly var node = ref parentArena[new Handle<ParentNode>(i)];
            enumerator.EnumerateChildren(in node, in _visitor, ref action);
            count++;
        }

        return count;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // CASCADE DECREMENT BENCHMARKS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Direct: increment all parent roots (so they have refcount 1), then decrement them all,
    /// triggering cascade through the hand-rolled handler.
    /// </summary>
    [Benchmark]
    public int CascadeDecrement_Direct()
    {
        var parentRC = _direct.ParentStore.RefCounts;
        var childRC = _direct.ChildStore.RefCounts;
        var parentArena = _direct.ParentStore.Arena;

        // Give every parent and its child a refcount of 1
        for (var i = 0; i < this.N; i++)
        {
            parentRC.Increment(_parentHandles[i]);
            ref readonly var node = ref parentArena[_parentHandles[i]];
            if (node.ChildRef.IsValid) childRC.Increment(node.ChildRef);
        }

        // Decrement all parent roots — cascades into child refcounts
        var handler = new DirectParentHandler(parentArena, _direct.ChildStore);
        for (var i = 0; i < this.N; i++)
            parentRC.Decrement(_parentHandles[i], handler);

        return this.N;
    }

    /// <summary>
    /// Visitor: same increment/decrement pattern, but using the visitor-composed handler.
    /// </summary>
    [Benchmark]
    public int CascadeDecrement_Visitor()
    {
        var parentRC = _visitor.ParentStore.RefCounts;
        var childRC = _visitor.ChildStore.RefCounts;
        var parentArena = _visitor.ParentStore.Arena;

        // Give every parent and its child a refcount of 1
        for (var i = 0; i < this.N; i++)
        {
            var h = new Handle<ParentNode>(i);
            parentRC.Increment(h);
            ref readonly var node = ref parentArena[h];
            if (node.ChildRef.IsValid) childRC.Increment(node.ChildRef);
        }

        // Decrement all parent roots — cascades through visitor-composed handler
        var handler = new VisitorParentHandler { Arena = parentArena, World = _visitor };
        for (var i = 0; i < this.N; i++)
            parentRC.Decrement(new Handle<ParentNode>(i), handler);

        return this.N;
    }
}
