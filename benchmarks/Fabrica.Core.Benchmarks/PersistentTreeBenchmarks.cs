using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Fabrica.Core.Memory;

namespace Fabrica.Core.Benchmarks;

/// <summary>
/// Benchmarks for the combined <see cref="UnsafeSlabArena{T}"/> + <see cref="RefCountTable"/> system exercising
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
        public int Left;
        public int Right;
    }

    // ── Cascade handler ──────────────────────────────────────────────────

    private struct TreeHandler(UnsafeSlabArena<TreeNode> arena) : RefCountTable.IRefCountHandler
    {
        public void OnFreed(int index, RefCountTable table)
        {
            ref readonly var node = ref arena[index];
            if (node.Left >= 0) table.Decrement(node.Left, this);
            if (node.Right >= 0) table.Decrement(node.Right, this);
            arena.Free(index);
        }
    }

    // ── Parameters ───────────────────────────────────────────────────────

    [Params(19)]
    public int TreeDepth;

    [Params(1_000, 10_000, 50_000)]
    public int ChangeCount;

    // ── State ────────────────────────────────────────────────────────────

    private UnsafeSlabArena<TreeNode> _arena = null!;
    private RefCountTable _refCounts = null!;
    private int _rootIndex;
    private int _nodeCount;

    private int[] _burstRoots = null!;

    // ── Setup (rebuilds the tree before each BDN iteration) ──────────────

    [IterationSetup]
    public void Setup()
    {
        _nodeCount = (1 << (TreeDepth + 1)) - 1;
        _arena = new UnsafeSlabArena<TreeNode>();
        _refCounts = new RefCountTable();

        var maxCapacity = _nodeCount + (ChangeCount * (TreeDepth + 1)) + 1024;
        _refCounts.EnsureCapacity(maxCapacity);

        for (var i = 0; i < _nodeCount; i++)
            _arena.Allocate();

        for (var i = 0; i < _nodeCount; i++)
        {
            var left = (2 * i) + 1;
            var right = (2 * i) + 2;
            _arena[i] = new TreeNode
            {
                Left = left < _nodeCount ? left : -1,
                Right = right < _nodeCount ? right : -1
            };
        }

        _refCounts.Increment(0);
        for (var i = 0; i < _nodeCount; i++)
        {
            ref readonly var node = ref _arena[i];
            if (node.Left >= 0) _refCounts.Increment(node.Left);
            if (node.Right >= 0) _refCounts.Increment(node.Right);
        }

        _rootIndex = 0;
        _burstRoots = new int[ChangeCount + 1];
    }

    // ── Path-copy: create a new version by modifying one leaf ────────────

    private int PathCopy(int currentRoot, int leafAddress)
    {
        Span<int> pathNodes = stackalloc int[TreeDepth + 1];
        Span<bool> wentLeft = stackalloc bool[TreeDepth];

        pathNodes[0] = currentRoot;
        for (var level = 0; level < TreeDepth; level++)
        {
            ref readonly var node = ref _arena[pathNodes[level]];
            var goLeft = ((leafAddress >> (TreeDepth - 1 - level)) & 1) == 0;
            wentLeft[level] = goLeft;
            pathNodes[level + 1] = goLeft ? node.Left : node.Right;
        }

        var newLeaf = _arena.Allocate();
        _arena[newLeaf] = new TreeNode { Left = -1, Right = -1 };

        var childOnPath = newLeaf;
        for (var level = TreeDepth - 1; level >= 0; level--)
        {
            ref readonly var orig = ref _arena[pathNodes[level]];
            var newIdx = _arena.Allocate();

            int left, right;
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
            if (left >= 0) _refCounts.Increment(left);
            if (right >= 0) _refCounts.Increment(right);

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
        var leafCount = 1 << TreeDepth;
        var handler = new TreeHandler(_arena);
        var root = _rootIndex;

        for (var i = 0; i < ChangeCount; i++)
        {
            var leaf = rng.Next(leafCount);
            var newRoot = PathCopy(root, leaf);
            _refCounts.Decrement(root, handler);
            root = newRoot;
        }

        return root;
    }

    /// <summary>
    /// Producer runs ahead: all changes accumulate before any release.
    /// Measures peak memory and the cost of the bulk release cascade.
    /// </summary>
    [Benchmark]
    public int Burst_AllChangesThenRelease()
    {
        var rng = new Random(42);
        var leafCount = 1 << TreeDepth;
        var handler = new TreeHandler(_arena);
        var roots = _burstRoots;
        roots[0] = _rootIndex;

        for (var i = 0; i < ChangeCount; i++)
        {
            var leaf = rng.Next(leafCount);
            roots[i + 1] = PathCopy(roots[i], leaf);
        }

        for (var i = 0; i < ChangeCount; i++)
            _refCounts.Decrement(roots[i], handler);

        return roots[ChangeCount];
    }

    /// <summary>
    /// Producer ahead by a fixed window: every W changes the consumer releases W old roots.
    /// Models the typical case where the consumer lags by a few frames.
    /// </summary>
    [Benchmark]
    public int Windowed_ProducerAheadBy100()
    {
        const int window = 100;
        var rng = new Random(42);
        var leafCount = 1 << TreeDepth;
        var handler = new TreeHandler(_arena);

        var roots = _burstRoots;
        roots[0] = _rootIndex;
        var produced = 0;
        var consumed = 0;

        while (produced < ChangeCount)
        {
            var batchEnd = Math.Min(produced + window, ChangeCount);
            for (var i = produced; i < batchEnd; i++)
            {
                var leaf = rng.Next(leafCount);
                roots[i + 1] = PathCopy(roots[i], leaf);
            }

            produced = batchEnd;

            var releaseEnd = produced - window;
            if (releaseEnd < 0) releaseEnd = 0;
            for (var i = consumed; i < releaseEnd; i++)
                _refCounts.Decrement(roots[i], handler);
            consumed = Math.Max(consumed, releaseEnd);
        }

        for (var i = consumed; i < ChangeCount; i++)
            _refCounts.Decrement(roots[i], handler);

        return roots[ChangeCount];
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
        public int Left;
        public int Right;
    }

    private struct TreeHandler(UnsafeSlabArena<TreeNode> arena) : RefCountTable.IRefCountHandler
    {
        public void OnFreed(int index, RefCountTable table)
        {
            ref readonly var node = ref arena[index];
            if (node.Left >= 0) table.Decrement(node.Left, this);
            if (node.Right >= 0) table.Decrement(node.Right, this);
            arena.Free(index);
        }
    }

    [Params(19)]
    public int TreeDepth;

    [Params(1_000)]
    public int N;

    private UnsafeSlabArena<TreeNode> _arena = null!;
    private RefCountTable _refCounts = null!;
    private int _rootIndex;
    private int _nodeCount;

    private int[] _preForkedRoots = null!;

    [IterationSetup(Targets = [nameof(Fork_Only), nameof(ForkThenRelease)])]
    public void SetupForFork()
    {
        BuildTree();
    }

    [IterationSetup(Target = nameof(Release_Only))]
    public void SetupForRelease()
    {
        BuildTree();

        var rng = new Random(42);
        var leafCount = 1 << TreeDepth;
        _preForkedRoots = new int[N + 1];
        _preForkedRoots[0] = _rootIndex;
        for (var i = 0; i < N; i++)
        {
            var leaf = rng.Next(leafCount);
            _preForkedRoots[i + 1] = PathCopy(_preForkedRoots[i], leaf);
        }
    }

    private void BuildTree()
    {
        _nodeCount = (1 << (TreeDepth + 1)) - 1;
        _arena = new UnsafeSlabArena<TreeNode>();
        _refCounts = new RefCountTable();
        _refCounts.EnsureCapacity(_nodeCount + (N * (TreeDepth + 1)) + 1024);

        for (var i = 0; i < _nodeCount; i++)
            _arena.Allocate();

        for (var i = 0; i < _nodeCount; i++)
        {
            var left = (2 * i) + 1;
            var right = (2 * i) + 2;
            _arena[i] = new TreeNode
            {
                Left = left < _nodeCount ? left : -1,
                Right = right < _nodeCount ? right : -1
            };
        }

        _refCounts.Increment(0);
        for (var i = 0; i < _nodeCount; i++)
        {
            ref readonly var node = ref _arena[i];
            if (node.Left >= 0) _refCounts.Increment(node.Left);
            if (node.Right >= 0) _refCounts.Increment(node.Right);
        }

        _rootIndex = 0;
    }

    private int PathCopy(int currentRoot, int leafAddress)
    {
        Span<int> pathNodes = stackalloc int[TreeDepth + 1];
        Span<bool> wentLeft = stackalloc bool[TreeDepth];

        pathNodes[0] = currentRoot;
        for (var level = 0; level < TreeDepth; level++)
        {
            ref readonly var node = ref _arena[pathNodes[level]];
            var goLeft = ((leafAddress >> (TreeDepth - 1 - level)) & 1) == 0;
            wentLeft[level] = goLeft;
            pathNodes[level + 1] = goLeft ? node.Left : node.Right;
        }

        var newLeaf = _arena.Allocate();
        _arena[newLeaf] = new TreeNode { Left = -1, Right = -1 };

        var childOnPath = newLeaf;
        for (var level = TreeDepth - 1; level >= 0; level--)
        {
            ref readonly var orig = ref _arena[pathNodes[level]];
            var newIdx = _arena.Allocate();

            int left, right;
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
            if (left >= 0) _refCounts.Increment(left);
            if (right >= 0) _refCounts.Increment(right);

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
        var leafCount = 1 << TreeDepth;
        var root = _rootIndex;

        for (var i = 0; i < N; i++)
        {
            var leaf = rng.Next(leafCount);
            root = PathCopy(root, leaf);
        }

        return root;
    }

    /// <summary>
    /// Release only: given N pre-forked versions, release all N old roots. Each release
    /// cascade-frees 20 old spine nodes. Divide Mean by N for per-release cost.
    /// </summary>
    [Benchmark]
    public int Release_Only()
    {
        var handler = new TreeHandler(_arena);
        for (var i = 0; i < N; i++)
            _refCounts.Decrement(_preForkedRoots[i], handler);
        return _preForkedRoots[N];
    }

    /// <summary>
    /// Fork + release combined: N iterations of path-copy then immediate release.
    /// Divide Mean by N for per-change cost.
    /// </summary>
    [Benchmark]
    public int ForkThenRelease()
    {
        var rng = new Random(42);
        var leafCount = 1 << TreeDepth;
        var handler = new TreeHandler(_arena);
        var root = _rootIndex;

        for (var i = 0; i < N; i++)
        {
            var leaf = rng.Next(leafCount);
            var newRoot = PathCopy(root, leaf);
            _refCounts.Decrement(root, handler);
            root = newRoot;
        }

        return root;
    }
}

/// <summary>
/// Raw allocation/release throughput: 1M elements through <see cref="UnsafeSlabArena{T}"/> and
/// <see cref="RefCountTable"/> with no tree structure (baseline overhead measurement).
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
public class RawAllocReleaseBenchmarks
{
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct Node
    {
        public long Value;
    }

    private struct NoChildHandler(UnsafeSlabArena<Node> arena) : RefCountTable.IRefCountHandler
    {
        public void OnFreed(int index, RefCountTable table) => arena.Free(index);
    }

    [Params(100_000, 1_000_000)]
    public int N;

    [Benchmark]
    public int ArenaOnly_AllocThenFree()
    {
        var arena = new UnsafeSlabArena<Node>();
        for (var i = 0; i < N; i++)
            arena.Allocate();
        for (var i = N - 1; i >= 0; i--)
            arena.Free(i);
        return arena.Allocate();
    }

    [Benchmark]
    public int ArenaAndRefCount_AllocThenFree()
    {
        var arena = new UnsafeSlabArena<Node>();
        var rc = new RefCountTable();
        rc.EnsureCapacity(N);

        for (var i = 0; i < N; i++)
        {
            arena.Allocate();
            rc.Increment(i);
        }

        var handler = new NoChildHandler(arena);
        for (var i = N - 1; i >= 0; i--)
            rc.Decrement(i, handler);

        return arena.Allocate();
    }

    [Benchmark(Baseline = true)]
    public int ArenaAndRefCount_SteadyState()
    {
        var arena = new UnsafeSlabArena<Node>();
        var rc = new RefCountTable();
        rc.EnsureCapacity(N);
        var handler = new NoChildHandler(arena);

        for (var i = 0; i < N; i++)
        {
            arena.Allocate();
            rc.Increment(i);
        }

        for (var i = N - 1; i >= 0; i--)
            rc.Decrement(i, handler);

        var last = 0;
        for (var i = 0; i < N; i++)
        {
            last = arena.Allocate();
            rc.Increment(last);
        }

        for (var i = 0; i < N; i++)
            rc.Decrement(i, handler);

        return last;
    }
}
