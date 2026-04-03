using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Fabrica.Core.Memory;

namespace Fabrica.Core.Benchmarks;

/// <summary>
/// Measures the overhead (if any) of the visitor pattern — where
/// <see cref="IChildAction.OnChild{TChild}"/> receives only <c>Handle&lt;TChild&gt;</c> — vs.
/// hand-rolled direct code.
///
/// The increment path is the primary focus: it's the hot path that the visitor pattern must
/// handle with zero overhead. The cascade-decrement path uses hand-rolled handlers in both
/// variants (cascade is an <see cref="RefCountTable{T}.IRefCountHandler"/> concern, not an
/// <see cref="IChildAction"/> concern).
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
    // VISITOR — IChildAction receives Handle only
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Captures the world's refcount tables and increments the appropriate one per child type.
    /// typeof(TChild) comparisons are JIT constants — dead branches are eliminated entirely.
    /// </summary>
    private struct IncrementAction(
        RefCountTable<ParentNode> parentRC,
        RefCountTable<ChildNode> childRC) : IChildAction
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnChild<TChild>(Handle<TChild> child) where TChild : struct
        {
            if (typeof(TChild) == typeof(ParentNode))
                parentRC.Increment(Unsafe.As<Handle<TChild>, Handle<ParentNode>>(ref child));
            else if (typeof(TChild) == typeof(ChildNode))
                childRC.Increment(Unsafe.As<Handle<TChild>, Handle<ChildNode>>(ref child));
        }
    }

    private struct ParentNodeEnumerator : IChildEnumerator<ParentNode, World>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnumerateChildren<TAction>(in ParentNode node, in World context, ref TAction action)
            where TAction : struct, IChildAction
        {
            if (node.LeftParent.IsValid) action.OnChild(node.LeftParent);
            if (node.RightParent.IsValid) action.OnChild(node.RightParent);
            if (node.ChildRef.IsValid) action.OnChild(node.ChildRef);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // DIRECT (hand-rolled) handlers — used for cascade-decrement baseline
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

    // ── Visitor-composed cascade handler ─────────────────────────────────

    private struct VisitorChildHandler(UnsafeSlabArena<ChildNode> arena) : RefCountTable<ChildNode>.IRefCountHandler
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

    private struct VisitorParentHandler : RefCountTable<ParentNode>.IRefCountHandler
    {
        public World World;
        public VisitorChildHandler ChildHandler;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnFreed(Handle<ParentNode> handle, RefCountTable<ParentNode> table)
        {
            ref readonly var node = ref this.World.ParentArena[handle];
            if (node.LeftParent.IsValid) table.Decrement(node.LeftParent, this);
            if (node.RightParent.IsValid) table.Decrement(node.RightParent, this);
            if (node.ChildRef.IsValid) this.World.ChildRefCounts.Decrement(node.ChildRef, this.ChildHandler);
            this.World.ParentArena.Free(handle);
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
        var action = new IncrementAction(_world.ParentRefCounts, _world.ChildRefCounts);
        var count = 0;

        for (var i = 0; i < this.N; i++)
        {
            ref readonly var node = ref parentArena[_parentHandles[i]];
            enumerator.EnumerateChildren(in node, in _world, ref action);
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
            ChildHandler = new VisitorChildHandler(_world.ChildArena),
        };
        for (var i = 0; i < this.N; i++)
            parentRC.Decrement(_parentHandles[i], handler);

        return this.N;
    }
}
