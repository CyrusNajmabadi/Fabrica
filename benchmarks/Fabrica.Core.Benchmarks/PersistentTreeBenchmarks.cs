using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Fabrica.Core.Memory;

namespace Fabrica.Core.Benchmarks;

/// <summary>
/// Benchmarks for the combined <see cref="UnsafeSlabArena{T}"/> + <see cref="RefCountTable{T}"/> system exercising
/// realistic persistent/functional tree workloads: building a ~1M-node binary tree, then simulating the
/// producer/consumer pattern of forking new versions (path-copy) and releasing old roots.
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
public class PersistentTreeBenchmarks
{
    // ── Node type ────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct TreeNode
    {
        public Handle<TreeNode> Left;
        public Handle<TreeNode> Right;
    }

    // ── Enumerator + raw RefCountTable cascade (not NodeStore) ───────────

    private struct TreeChildEnumerator : INodeChildEnumerator<TreeNode>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void EnumerateChildren<TVisitor>(in TreeNode node, ref TVisitor visitor)
            where TVisitor : struct, INodeVisitor
        {
            if (node.Left.IsValid) visitor.Visit(node.Left);
            if (node.Right.IsValid) visitor.Visit(node.Right);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void EnumerateChildren<TVisitor, TContext>(in TreeNode node, in TContext context, ref TVisitor visitor)
            where TVisitor : struct, INodeVisitor<TContext>
        {
            if (node.Left.IsValid) visitor.Visit(node.Left, in context);
            if (node.Right.IsValid) visitor.Visit(node.Right, in context);
        }
    }

    private void ReleaseRoot(Handle<TreeNode> root)
    {
        var pending = new Stack<Handle<TreeNode>>();
        if (_refCounts.Decrement(root))
            pending.Push(root);
        this.ReleaseCascade(pending);
    }

    private void ReleaseRootsBatch(ReadOnlySpan<Handle<TreeNode>> roots)
    {
        var hitZero = new UnsafeStack<Handle<TreeNode>>(roots.Length + 1);
        _refCounts.DecrementBatch(roots, hitZero);
        var pending = new Stack<Handle<TreeNode>>();
        while (hitZero.TryPop(out var h))
            pending.Push(h);
        this.ReleaseCascade(pending);
    }

    private void ReleaseCascade(Stack<Handle<TreeNode>> pending)
    {
        while (pending.TryPop(out var current))
        {
            ref readonly var node = ref _arena[current];
            if (node.Left.IsValid && _refCounts.Decrement(node.Left))
                pending.Push(node.Left);
            if (node.Right.IsValid && _refCounts.Decrement(node.Right))
                pending.Push(node.Right);
            _arena.Free(current);
        }
    }

    // ── Parameters ───────────────────────────────────────────────────────

    [Params(19)]
    public int TreeDepth { get; set; }

    [Params(1_000, 10_000, 50_000)]
    public int ChangeCount { get; set; }

    // ── State ────────────────────────────────────────────────────────────

    private UnsafeSlabArena<TreeNode> _arena = null!;
    private RefCountTable<TreeNode> _refCounts = null!;
    private Handle<TreeNode> _root;
    private int _nodeCount;

    private Handle<TreeNode>[] _burstRoots = null!;

    // ── Setup (rebuilds the tree before each BDN iteration) ──────────────

    [IterationSetup]
    public void Setup()
    {
        _nodeCount = (1 << (this.TreeDepth + 1)) - 1;
        _arena = new UnsafeSlabArena<TreeNode>();
        _refCounts = new RefCountTable<TreeNode>();

        var maxCapacity = _nodeCount + (this.ChangeCount * (this.TreeDepth + 1)) + 1024;
        _refCounts.EnsureCapacity(maxCapacity);

        for (var i = 0; i < _nodeCount; i++)
            _arena.Allocate();

        for (var i = 0; i < _nodeCount; i++)
        {
            var left = (2 * i) + 1;
            var right = (2 * i) + 2;
            _arena[new Handle<TreeNode>(i)] = new TreeNode
            {
                Left = left < _nodeCount ? new Handle<TreeNode>(left) : Handle<TreeNode>.None,
                Right = right < _nodeCount ? new Handle<TreeNode>(right) : Handle<TreeNode>.None
            };
        }

        _refCounts.Increment(new Handle<TreeNode>(0));
        for (var i = 0; i < _nodeCount; i++)
        {
            ref readonly var node = ref _arena[new Handle<TreeNode>(i)];
            if (node.Left.IsValid) _refCounts.Increment(node.Left);
            if (node.Right.IsValid) _refCounts.Increment(node.Right);
        }

        _root = new Handle<TreeNode>(0);
        _burstRoots = new Handle<TreeNode>[this.ChangeCount + 1];
    }

    // ── Path-copy: create a new version by modifying one leaf ────────────

    private Handle<TreeNode> PathCopy(Handle<TreeNode> currentRoot, int leafAddress)
    {
        Span<Handle<TreeNode>> pathNodes = stackalloc Handle<TreeNode>[this.TreeDepth + 1];
        Span<bool> wentLeft = stackalloc bool[this.TreeDepth];

        pathNodes[0] = currentRoot;
        for (var level = 0; level < this.TreeDepth; level++)
        {
            ref readonly var node = ref _arena[pathNodes[level]];
            var goLeft = ((leafAddress >> (this.TreeDepth - 1 - level)) & 1) == 0;
            wentLeft[level] = goLeft;
            pathNodes[level + 1] = goLeft ? node.Left : node.Right;
        }

        var newLeaf = _arena.Allocate();
        _arena[newLeaf] = new TreeNode { Left = Handle<TreeNode>.None, Right = Handle<TreeNode>.None };

        var childOnPath = newLeaf;
        for (var level = this.TreeDepth - 1; level >= 0; level--)
        {
            ref readonly var orig = ref _arena[pathNodes[level]];
            var newIdx = _arena.Allocate();

            Handle<TreeNode> left, right;
            if (wentLeft[level])
            {
                left = childOnPath;
                right = orig.Right;
            }
            else
            {
                left = orig.Left;
                right = childOnPath;
            }

            _arena[newIdx] = new TreeNode { Left = left, Right = right };
            if (left.IsValid) _refCounts.Increment(left);
            if (right.IsValid) _refCounts.Increment(right);

            childOnPath = newIdx;
        }

        _refCounts.Increment(childOnPath);
        return childOnPath;
    }

    // ═══════════════════════ Multi-change benchmarks ═════════════════════

    /// <summary>
    /// Producer/consumer lock-step: each change immediately releases the old root.
    /// Freed spine nodes are recycled by the next change. Memory stays flat at ~1M nodes.
    /// </summary>
    [Benchmark(Baseline = true)]
    public int Interleaved_RandomLeaf()
    {
        var rng = new Random(42);
        var leafCount = 1 << this.TreeDepth;
        var root = _root;

        for (var i = 0; i < this.ChangeCount; i++)
        {
            var leaf = rng.Next(leafCount);
            var newRoot = this.PathCopy(root, leaf);
            this.ReleaseRoot(root);
            root = newRoot;
        }

        return root.Index;
    }

    /// <summary>
    /// Producer runs ahead: all changes accumulate before any release.
    /// Measures peak memory and the cost of the bulk release cascade.
    /// </summary>
    [Benchmark]
    public int Burst_AllChangesThenRelease()
    {
        var rng = new Random(42);
        var leafCount = 1 << this.TreeDepth;
        var roots = _burstRoots;
        roots[0] = _root;

        for (var i = 0; i < this.ChangeCount; i++)
        {
            var leaf = rng.Next(leafCount);
            roots[i + 1] = this.PathCopy(roots[i], leaf);
        }

        this.ReleaseRootsBatch(roots.AsSpan(0, this.ChangeCount));

        return roots[this.ChangeCount].Index;
    }

    /// <summary>
    /// Producer ahead by a fixed window: every W changes the consumer releases W old roots.
    /// Models the typical case where the consumer lags by a few frames.
    /// </summary>
    [Benchmark]
    public int Windowed_ProducerAheadBy100()
    {
        const int Window = 100;
        var rng = new Random(42);
        var leafCount = 1 << this.TreeDepth;

        var roots = _burstRoots;
        roots[0] = _root;
        var produced = 0;
        var consumed = 0;
        while (produced < this.ChangeCount)
        {
            var batchEnd = Math.Min(produced + Window, this.ChangeCount);
            for (var i = produced; i < batchEnd; i++)
            {
                var leaf = rng.Next(leafCount);
                roots[i + 1] = this.PathCopy(roots[i], leaf);
            }

            produced = batchEnd;

            var releaseEnd = produced - Window;
            if (releaseEnd < 0) releaseEnd = 0;
            if (releaseEnd > consumed)
                this.ReleaseRootsBatch(roots.AsSpan(consumed, releaseEnd - consumed));
            consumed = Math.Max(consumed, releaseEnd);
        }

        if (this.ChangeCount > consumed)
            this.ReleaseRootsBatch(roots.AsSpan(consumed, this.ChangeCount - consumed));

        return roots[this.ChangeCount].Index;
    }
}

/// <summary>
/// Microbenchmarks isolating the cost of fork (path-copy) and release (old-spine cascade) on a ~1M-node
/// persistent binary tree. Runs N iterations internally for stable measurements. Divide Mean by N to get
/// the per-operation cost.
///
/// Fork: walk root→leaf (20 levels), allocate 20 new spine nodes, increment 40 shared-child refcounts.
/// Release: decrement old root, cascade-free 20 old spine nodes, decrement 40 shared-child refcounts.
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
public class SingleForkReleaseBenchmarks
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TreeNode
    {
        public Handle<TreeNode> Left;
        public Handle<TreeNode> Right;
    }

    private struct TreeNodeOps : INodeChildEnumerator<TreeNode>, INodeVisitor
    {
        internal NodeStore<TreeNode, TreeNodeOps> Store;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void EnumerateChildren<TVisitor>(in TreeNode node, ref TVisitor visitor)
            where TVisitor : struct, INodeVisitor
        {
            if (node.Left.IsValid) visitor.Visit(node.Left);
            if (node.Right.IsValid) visitor.Visit(node.Right);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void EnumerateChildren<TVisitor, TContext>(in TreeNode node, in TContext context, ref TVisitor visitor)
            where TVisitor : struct, INodeVisitor<TContext>
        {
            if (node.Left.IsValid) visitor.Visit(node.Left, in context);
            if (node.Right.IsValid) visitor.Visit(node.Right, in context);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void EnumerateRefChildren<TVisitor>(ref TreeNode node, ref TVisitor visitor)
            where TVisitor : struct, INodeVisitor
        {
            ref var left = ref node.Left;
            if (left.IsValid) visitor.VisitRef(ref left);
            ref var right = ref node.Right;
            if (right.IsValid) visitor.VisitRef(ref right);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void EnumerateRefChildren<TVisitor, TContext>(ref TreeNode node, in TContext context, ref TVisitor visitor)
            where TVisitor : struct, INodeVisitor<TContext>
        {
            ref var left = ref node.Left;
            if (left.IsValid) visitor.VisitRef(ref left, in context);
            ref var right = ref node.Right;
            if (right.IsValid) visitor.VisitRef(ref right, in context);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Visit<TChild>(Handle<TChild> child)
            where TChild : struct
        {
            if (typeof(TChild) == typeof(TreeNode))
            {
                var c = child;
                Store.DecrementRefCount(Unsafe.As<Handle<TChild>, Handle<TreeNode>>(ref c));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void VisitRef<TChild>(ref Handle<TChild> child)
            where TChild : struct
        {
            if (typeof(TChild) == typeof(TreeNode))
                Store.DecrementRefCount(Unsafe.As<Handle<TChild>, Handle<TreeNode>>(ref child));
        }
    }

    [Params(19)]
    public int TreeDepth { get; set; }

    [Params(1_000)]
    public int N { get; set; }

    private UnsafeSlabArena<TreeNode> _arena = null!;
    private RefCountTable<TreeNode> _refCounts = null!;
    private NodeStore<TreeNode, TreeNodeOps> _store = null!;
    private Handle<TreeNode> _root;
    private int _nodeCount;

    private Handle<TreeNode>[] _preForkedRoots = null!;

    [IterationSetup(Targets = [nameof(Fork_Only), nameof(ForkThenRelease)])]
    public void SetupForFork() => this.BuildTree();

    [IterationSetup(Target = nameof(Release_Only))]
    public void SetupForRelease()
    {
        this.BuildTree();

        var rng = new Random(42);
        var leafCount = 1 << this.TreeDepth;
        _preForkedRoots = new Handle<TreeNode>[this.N + 1];
        _preForkedRoots[0] = _root;
        for (var i = 0; i < this.N; i++)
        {
            var leaf = rng.Next(leafCount);
            _preForkedRoots[i + 1] = this.PathCopy(_preForkedRoots[i], leaf);
        }
    }

    private void BuildTree()
    {
        _nodeCount = (1 << (this.TreeDepth + 1)) - 1;
        _arena = new UnsafeSlabArena<TreeNode>();
        _refCounts = new RefCountTable<TreeNode>();
        _store = new NodeStore<TreeNode, TreeNodeOps>(_arena, _refCounts, default);
        _refCounts.EnsureCapacity(_nodeCount + (this.N * (this.TreeDepth + 1)) + 1024);

        for (var i = 0; i < _nodeCount; i++)
            _arena.Allocate();

        for (var i = 0; i < _nodeCount; i++)
        {
            var left = (2 * i) + 1;
            var right = (2 * i) + 2;
            _arena[new Handle<TreeNode>(i)] = new TreeNode
            {
                Left = left < _nodeCount ? new Handle<TreeNode>(left) : Handle<TreeNode>.None,
                Right = right < _nodeCount ? new Handle<TreeNode>(right) : Handle<TreeNode>.None
            };
        }

        _refCounts.Increment(new Handle<TreeNode>(0));
        for (var i = 0; i < _nodeCount; i++)
        {
            ref readonly var node = ref _arena[new Handle<TreeNode>(i)];
            if (node.Left.IsValid) _refCounts.Increment(node.Left);
            if (node.Right.IsValid) _refCounts.Increment(node.Right);
        }

        _root = new Handle<TreeNode>(0);

        _store.SetNodeOps(new TreeNodeOps { Store = _store });
    }

    private Handle<TreeNode> PathCopy(Handle<TreeNode> currentRoot, int leafAddress)
    {
        Span<Handle<TreeNode>> pathNodes = stackalloc Handle<TreeNode>[this.TreeDepth + 1];
        Span<bool> wentLeft = stackalloc bool[this.TreeDepth];

        pathNodes[0] = currentRoot;
        for (var level = 0; level < this.TreeDepth; level++)
        {
            ref readonly var node = ref _arena[pathNodes[level]];
            var goLeft = ((leafAddress >> (this.TreeDepth - 1 - level)) & 1) == 0;
            wentLeft[level] = goLeft;
            pathNodes[level + 1] = goLeft ? node.Left : node.Right;
        }

        var newLeaf = _arena.Allocate();
        _arena[newLeaf] = new TreeNode { Left = Handle<TreeNode>.None, Right = Handle<TreeNode>.None };

        var childOnPath = newLeaf;
        for (var level = this.TreeDepth - 1; level >= 0; level--)
        {
            ref readonly var orig = ref _arena[pathNodes[level]];
            var newIdx = _arena.Allocate();

            Handle<TreeNode> left, right;
            if (wentLeft[level])
            {
                left = childOnPath;
                right = orig.Right;
            }
            else
            {
                left = orig.Left;
                right = childOnPath;
            }

            _arena[newIdx] = new TreeNode { Left = left, Right = right };
            if (left.IsValid) _refCounts.Increment(left);
            if (right.IsValid) _refCounts.Increment(right);

            childOnPath = newIdx;
        }

        _refCounts.Increment(childOnPath);
        return childOnPath;
    }

    // ═══════════════════════ Isolated operations ═════════════════════════

    /// <summary>
    /// Fork only: N path-copies (no releases). Each creates 20 new spine nodes + increments
    /// 40 shared-child refcounts. Divide Mean by N for per-fork cost.
    /// </summary>
    [Benchmark(Baseline = true)]
    public int Fork_Only()
    {
        var rng = new Random(42);
        var leafCount = 1 << this.TreeDepth;
        var root = _root;

        for (var i = 0; i < this.N; i++)
        {
            var leaf = rng.Next(leafCount);
            root = this.PathCopy(root, leaf);
        }

        return root.Index;
    }

    /// <summary>
    /// Release only: given N pre-forked versions, release all N old roots. Each release
    /// cascade-frees 20 old spine nodes. Divide Mean by N for per-release cost.
    /// </summary>
    [Benchmark]
    public int Release_Only()
    {
        _store.DecrementRoots(_preForkedRoots.AsSpan(0, this.N));
        return _preForkedRoots[this.N].Index;
    }

    /// <summary>
    /// Fork + release combined: N iterations of path-copy then immediate release.
    /// Divide Mean by N for per-change cost.
    /// </summary>
    [Benchmark]
    public int ForkThenRelease()
    {
        var rng = new Random(42);
        var leafCount = 1 << this.TreeDepth;
        var root = _root;

        for (var i = 0; i < this.N; i++)
        {
            var leaf = rng.Next(leafCount);
            var newRoot = this.PathCopy(root, leaf);
            _store.DecrementRefCount(root);
            root = newRoot;
        }

        return root.Index;
    }
}

/// <summary>
/// Raw allocation/release throughput: 1M elements through <see cref="UnsafeSlabArena{T}"/> and
/// <see cref="RefCountTable{T}"/> with no tree structure (baseline overhead measurement).
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
public class RawAllocReleaseBenchmarks
{
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct Node
    {
        public long Value { get; set; }
    }

    [Params(100_000, 1_000_000)]
    public int N { get; set; }

    [Benchmark]
    public int ArenaOnly_AllocThenFree()
    {
        var arena = new UnsafeSlabArena<Node>();
        for (var i = 0; i < this.N; i++)
            arena.Allocate();
        for (var i = this.N - 1; i >= 0; i--)
            arena.Free(new Handle<Node>(i));
        return arena.Allocate().Index;
    }

    [Benchmark]
    public int ArenaAndRefCount_AllocThenFree()
    {
        var arena = new UnsafeSlabArena<Node>();
        var rc = new RefCountTable<Node>();
        rc.EnsureCapacity(this.N);

        for (var i = 0; i < this.N; i++)
        {
            arena.Allocate();
            rc.Increment(new Handle<Node>(i));
        }

        for (var i = this.N - 1; i >= 0; i--)
        {
            if (rc.Decrement(new Handle<Node>(i)))
                arena.Free(new Handle<Node>(i));
        }

        return arena.Allocate().Index;
    }

    [Benchmark(Baseline = true)]
    public int ArenaAndRefCount_SteadyState()
    {
        var arena = new UnsafeSlabArena<Node>();
        var rc = new RefCountTable<Node>();
        rc.EnsureCapacity(this.N);

        for (var i = 0; i < this.N; i++)
        {
            arena.Allocate();
            rc.Increment(new Handle<Node>(i));
        }

        for (var i = this.N - 1; i >= 0; i--)
        {
            if (rc.Decrement(new Handle<Node>(i)))
                arena.Free(new Handle<Node>(i));
        }

        Handle<Node> last = default;
        for (var i = 0; i < this.N; i++)
        {
            last = arena.Allocate();
            rc.Increment(last);
        }

        for (var i = 0; i < this.N; i++)
        {
            if (rc.Decrement(new Handle<Node>(i)))
                arena.Free(new Handle<Node>(i));
        }

        return last.Index;
    }
}
