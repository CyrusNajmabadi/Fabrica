using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Fabrica.Core.Memory;

namespace Fabrica.Core.Benchmarks;

/// <summary>
/// Measures the overhead (if any) of the visitor pattern — where
/// <see cref="INodeVisitor.Visit{TChild}"/> receives only <c>Handle&lt;TChild&gt;</c> — vs.
/// hand-rolled direct code.
///
/// Both the increment path (hot path for adding children) and the cascade-decrement path
/// (OnFreed handler) are benchmarked. The "Visitor" variants use <see cref="INodeChildEnumerator{TNode}"/>
/// and <see cref="INodeVisitor"/> / <see cref="RefCountTable{TNode}.DecrementNodeRefCountVisitor{THandler}"/> throughout.
/// The "Direct" variants hand-roll all child traversal.
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

    // ── World context ────────────────────────────────────────────────────

    private struct World
    {
        public UnsafeSlabArena<ParentNode> ParentArena;
        public RefCountTable<ParentNode> ParentRefCounts;
        public UnsafeSlabArena<ChildNode> ChildArena;
        public RefCountTable<ChildNode> ChildRefCounts;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // VISITOR — INodeVisitor receives Handle only
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Captures the world's refcount tables and increments the appropriate one per child type.
    /// typeof(TChild) comparisons are JIT constants — dead branches are eliminated entirely.
    /// </summary>
    private struct IncrementNodeVisitor(
        RefCountTable<ParentNode> parentRC,
        RefCountTable<ChildNode> childRC) : INodeVisitor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Visit<TChild>(Handle<TChild> child) where TChild : struct
        {
            if (typeof(TChild) == typeof(ParentNode))
                parentRC.Increment(Unsafe.As<Handle<TChild>, Handle<ParentNode>>(ref child));
            else if (typeof(TChild) == typeof(ChildNode))
                childRC.Increment(Unsafe.As<Handle<TChild>, Handle<ChildNode>>(ref child));
        }
    }

    private struct ParentNodeEnumerator : INodeChildEnumerator<ParentNode>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void EnumerateChildren<TAction>(in ParentNode node, ref TAction visitor)
            where TAction : struct, INodeVisitor
        {
            if (node.LeftParent.IsValid) visitor.Visit(node.LeftParent);
            if (node.RightParent.IsValid) visitor.Visit(node.RightParent);
            if (node.ChildRef.IsValid) visitor.Visit(node.ChildRef);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void EnumerateChildren<TAction, TContext>(in ParentNode node, in TContext context, ref TAction visitor)
            where TAction : struct, INodeVisitor<TContext>
        {
            if (node.LeftParent.IsValid) visitor.Visit(node.LeftParent, in context);
            if (node.RightParent.IsValid) visitor.Visit(node.RightParent, in context);
            if (node.ChildRef.IsValid) visitor.Visit(node.ChildRef, in context);
        }
    }

    private struct ChildNodeEnumerator : INodeChildEnumerator<ChildNode>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void EnumerateChildren<TAction>(in ChildNode node, ref TAction visitor)
            where TAction : struct, INodeVisitor
        {
            if (node.Left.IsValid) visitor.Visit(node.Left);
            if (node.Right.IsValid) visitor.Visit(node.Right);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void EnumerateChildren<TAction, TContext>(in ChildNode node, in TContext context, ref TAction visitor)
            where TAction : struct, INodeVisitor<TContext>
        {
            if (node.Left.IsValid) visitor.Visit(node.Left, in context);
            if (node.Right.IsValid) visitor.Visit(node.Right, in context);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // DIRECT (hand-rolled) handlers — cascade-decrement baseline
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
        RefCountTable<ChildNode> childRefCounts,
        DirectChildHandler childHandler) : RefCountTable<ParentNode>.IRefCountHandler
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void OnFreed(Handle<ParentNode> handle, RefCountTable<ParentNode> table)
        {
            ref readonly var node = ref parentArena[handle];
            if (node.LeftParent.IsValid) table.Decrement(node.LeftParent, this);
            if (node.RightParent.IsValid) table.Decrement(node.RightParent, this);
            if (node.ChildRef.IsValid) childRefCounts.Decrement(node.ChildRef, childHandler);
            parentArena.Free(handle);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // VISITOR — enumerator-based cascade handlers
    // ═══════════════════════════════════════════════════════════════════════

    private struct VisitorChildHandler(UnsafeSlabArena<ChildNode> arena, ChildNodeEnumerator enumerator) : RefCountTable<ChildNode>.IRefCountHandler
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnFreed(Handle<ChildNode> handle, RefCountTable<ChildNode> table)
        {
            ref readonly var node = ref arena[handle];
            var visitor = new RefCountTable<ChildNode>.DecrementNodeRefCountVisitor<VisitorChildHandler>(table, this);
            enumerator.EnumerateChildren(in node, ref visitor);
            arena.Free(handle);
        }
    }

    /// <summary>
    /// Cross-type decrement action for parent nodes: dispatches to the correct table by type.
    /// </summary>
    private struct VisitorParentDecrementNodeVisitor(
        RefCountTable<ParentNode> parentTable,
        VisitorParentHandler parentHandler,
        RefCountTable<ChildNode> childTable,
        VisitorChildHandler childHandler) : INodeVisitor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Visit<TChild>(Handle<TChild> child) where TChild : struct
        {
            if (typeof(TChild) == typeof(ParentNode))
                this.DecrementParent(Unsafe.As<Handle<TChild>, Handle<ParentNode>>(ref child));
            else if (typeof(TChild) == typeof(ChildNode))
                this.DecrementChild(Unsafe.As<Handle<TChild>, Handle<ChildNode>>(ref child));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly void DecrementParent(Handle<ParentNode> child)
        {
            Debug.Assert(child.IsValid);
            parentTable.Decrement(child, parentHandler);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly void DecrementChild(Handle<ChildNode> child)
        {
            Debug.Assert(child.IsValid);
            childTable.Decrement(child, childHandler);
        }
    }

    private struct VisitorParentHandler : RefCountTable<ParentNode>.IRefCountHandler
    {
        public World World;
        public VisitorChildHandler ChildHandler;
        public ParentNodeEnumerator Enumerator;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnFreed(Handle<ParentNode> handle, RefCountTable<ParentNode> table)
        {
            ref readonly var node = ref World.ParentArena[handle];
            var visitor = new VisitorParentDecrementNodeVisitor(table, this, World.ChildRefCounts, ChildHandler);
            Enumerator.EnumerateChildren(in node, ref visitor);
            World.ParentArena.Free(handle);
        }
    }

    // ── Parameters ───────────────────────────────────────────────────────

    [Params(1_000, 10_000, 100_000)]
    public int N { get; set; }

    // ── State ────────────────────────────────────────────────────────────

    private World _world;
    private Handle<ParentNode>[] _parentHandles = null!;

    [IterationSetup]
    public void Setup()
    {
        var childArena = new UnsafeSlabArena<ChildNode>();
        var childRC = new RefCountTable<ChildNode>();
        var parentArena = new UnsafeSlabArena<ParentNode>();
        var parentRC = new RefCountTable<ParentNode>();

        _world = new World
        {
            ParentArena = parentArena,
            ParentRefCounts = parentRC,
            ChildArena = childArena,
            ChildRefCounts = childRC,
        };

        childRC.EnsureCapacity(this.N * 2);
        parentRC.EnsureCapacity(this.N);

        _parentHandles = new Handle<ParentNode>[this.N];

        for (var i = 0; i < this.N; i++)
        {
            var h = childArena.Allocate();
            childArena[h] = new ChildNode { Left = Handle<ChildNode>.None, Right = Handle<ChildNode>.None };
        }

        for (var i = 0; i < this.N; i++)
        {
            var h = parentArena.Allocate();
            parentArena[h] = new ParentNode
            {
                LeftParent = Handle<ParentNode>.None,
                RightParent = Handle<ParentNode>.None,
                ChildRef = new Handle<ChildNode>(i),
            };
            _parentHandles[i] = h;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // INCREMENT BENCHMARKS
    // ═══════════════════════════════════════════════════════════════════════

    [Benchmark(Baseline = true)]
    public int IncrementChildren_Direct()
    {
        var parentArena = _world.ParentArena;
        var parentRC = _world.ParentRefCounts;
        var childRC = _world.ChildRefCounts;
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

    [Benchmark]
    public int IncrementChildren_Visitor()
    {
        var parentArena = _world.ParentArena;
        var enumerator = default(ParentNodeEnumerator);
        var visitor = new IncrementNodeVisitor(_world.ParentRefCounts, _world.ChildRefCounts);
        var count = 0;

        for (var i = 0; i < this.N; i++)
        {
            ref readonly var node = ref parentArena[_parentHandles[i]];
            enumerator.EnumerateChildren(in node, ref visitor);
            count++;
        }

        return count;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // CASCADE DECREMENT BENCHMARKS
    // ═══════════════════════════════════════════════════════════════════════

    [Benchmark]
    public int CascadeDecrement_Direct()
    {
        var parentRC = _world.ParentRefCounts;
        var childRC = _world.ChildRefCounts;
        var parentArena = _world.ParentArena;

        for (var i = 0; i < this.N; i++)
        {
            parentRC.Increment(_parentHandles[i]);
            ref readonly var node = ref parentArena[_parentHandles[i]];
            if (node.ChildRef.IsValid) childRC.Increment(node.ChildRef);
        }

        var handler = new DirectParentHandler(
            parentArena,
            childRC,
            new DirectChildHandler(_world.ChildArena));
        for (var i = 0; i < this.N; i++)
            parentRC.Decrement(_parentHandles[i], handler);

        return this.N;
    }

    [Benchmark]
    public int CascadeDecrement_Visitor()
    {
        var parentRC = _world.ParentRefCounts;
        var childRC = _world.ChildRefCounts;
        var parentArena = _world.ParentArena;

        for (var i = 0; i < this.N; i++)
        {
            parentRC.Increment(_parentHandles[i]);
            ref readonly var node = ref parentArena[_parentHandles[i]];
            if (node.ChildRef.IsValid) childRC.Increment(node.ChildRef);
        }

        var handler = new VisitorParentHandler
        {
            World = _world,
            ChildHandler = new VisitorChildHandler(_world.ChildArena, default),
            Enumerator = default,
        };
        for (var i = 0; i < this.N; i++)
            parentRC.Decrement(_parentHandles[i], handler);

        return this.N;
    }
}
