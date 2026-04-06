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
/// (caller-driven cascade after <see cref="RefCountTable{T}.Decrement(Handle{T})"/> returns
/// <c>true</c>) are benchmarked. The "Visitor" variants use <see cref="INodeOps{TNode}"/>
/// and <see cref="INodeVisitor"/> to traverse children. The "Direct" variants hand-roll all child
/// traversal.
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

    private struct ParentNodeEnumerator : INodeOps<ParentNode>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void EnumerateChildren<TAction>(in ParentNode node, ref TAction visitor)
            where TAction : struct, INodeVisitor
        {
            if (node.LeftParent.IsValid) visitor.Visit(node.LeftParent);
            if (node.RightParent.IsValid) visitor.Visit(node.RightParent);
            if (node.ChildRef.IsValid) visitor.Visit(node.ChildRef);
        }
    }

    private struct ChildNodeEnumerator : INodeOps<ChildNode>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void EnumerateChildren<TAction>(in ChildNode node, ref TAction visitor)
            where TAction : struct, INodeVisitor
        {
            if (node.Left.IsValid) visitor.Visit(node.Left);
            if (node.Right.IsValid) visitor.Visit(node.Right);
        }
    }

    private static void ProcessChildCascade(ref World w, Handle<ChildNode> h)
    {
        ref readonly var cn = ref w.ChildArena[h];
        if (cn.Left.IsValid && w.ChildRefCounts.Decrement(cn.Left))
            ProcessChildCascade(ref w, cn.Left);
        if (cn.Right.IsValid && w.ChildRefCounts.Decrement(cn.Right))
            ProcessChildCascade(ref w, cn.Right);
        w.ChildArena.Free(h);
    }

    private static void ProcessParentCascade(ref World w, Handle<ParentNode> h)
    {
        ref readonly var pn = ref w.ParentArena[h];
        if (pn.LeftParent.IsValid && w.ParentRefCounts.Decrement(pn.LeftParent))
            ProcessParentCascade(ref w, pn.LeftParent);
        if (pn.RightParent.IsValid && w.ParentRefCounts.Decrement(pn.RightParent))
            ProcessParentCascade(ref w, pn.RightParent);
        if (pn.ChildRef.IsValid && w.ChildRefCounts.Decrement(pn.ChildRef))
            ProcessChildCascade(ref w, pn.ChildRef);
        w.ParentArena.Free(h);
    }

    private struct ParentDecVisitor : INodeVisitor
    {
        public World World;
        public ParentNodeEnumerator ParentEnum;
        public ChildNodeEnumerator ChildEnum;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Visit<TChild>(Handle<TChild> child) where TChild : struct
        {
            if (typeof(TChild) == typeof(ParentNode))
            {
                var h = Unsafe.As<Handle<TChild>, Handle<ParentNode>>(ref child);
                if (World.ParentRefCounts.Decrement(h))
                    ProcessParentCascadeVisitor(ref World, ref ParentEnum, ref ChildEnum, h);
            }
            else if (typeof(TChild) == typeof(ChildNode))
            {
                var h = Unsafe.As<Handle<TChild>, Handle<ChildNode>>(ref child);
                if (World.ChildRefCounts.Decrement(h))
                    ProcessChildCascadeVisitor(ref World, ref ChildEnum, h);
            }
        }
    }

    private struct ChildDecVisitor : INodeVisitor
    {
        public World World;
        public ChildNodeEnumerator ChildEnum;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Visit<TChild>(Handle<TChild> child) where TChild : struct
        {
            if (typeof(TChild) == typeof(ChildNode))
            {
                var h = Unsafe.As<Handle<TChild>, Handle<ChildNode>>(ref child);
                if (World.ChildRefCounts.Decrement(h))
                    ProcessChildCascadeVisitor(ref World, ref ChildEnum, h);
            }
        }
    }

    private static void ProcessChildCascadeVisitor(ref World w, ref ChildNodeEnumerator ce, Handle<ChildNode> h)
    {
        ref readonly var cn = ref w.ChildArena[h];
        var v = new ChildDecVisitor { World = w, ChildEnum = ce };
        ce.EnumerateChildren(in cn, ref v);
        w.ChildArena.Free(h);
    }

    private static void ProcessParentCascadeVisitor(ref World w, ref ParentNodeEnumerator pe, ref ChildNodeEnumerator ce, Handle<ParentNode> h)
    {
        ref readonly var pn = ref w.ParentArena[h];
        var v = new ParentDecVisitor { World = w, ParentEnum = pe, ChildEnum = ce };
        pe.EnumerateChildren(in pn, ref v);
        w.ParentArena.Free(h);
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
        var w = _world;

        for (var i = 0; i < this.N; i++)
        {
            w.ParentRefCounts.Increment(_parentHandles[i]);
            ref readonly var node = ref w.ParentArena[_parentHandles[i]];
            if (node.ChildRef.IsValid) w.ChildRefCounts.Increment(node.ChildRef);
        }

        for (var i = 0; i < this.N; i++)
        {
            if (w.ParentRefCounts.Decrement(_parentHandles[i]))
                ProcessParentCascade(ref w, _parentHandles[i]);
        }

        return this.N;
    }

    [Benchmark]
    public int CascadeDecrement_Visitor()
    {
        var w = _world;
        var pe = default(ParentNodeEnumerator);
        var ce = default(ChildNodeEnumerator);

        for (var i = 0; i < this.N; i++)
        {
            w.ParentRefCounts.Increment(_parentHandles[i]);
            ref readonly var node = ref w.ParentArena[_parentHandles[i]];
            if (node.ChildRef.IsValid) w.ChildRefCounts.Increment(node.ChildRef);
        }

        for (var i = 0; i < this.N; i++)
        {
            if (w.ParentRefCounts.Decrement(_parentHandles[i]))
                ProcessParentCascadeVisitor(ref w, ref pe, ref ce, _parentHandles[i]);
        }

        return this.N;
    }
}
