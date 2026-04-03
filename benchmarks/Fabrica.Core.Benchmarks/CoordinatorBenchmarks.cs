using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Fabrica.Core.Memory;

namespace Fabrica.Core.Benchmarks;

/// <summary>
/// Benchmarks for the <see cref="ArenaCoordinator{TNode}"/> pipeline measuring the full merge path: buffer
/// fill → merge (allocate globals, fixup, increment children) → release processing (cascade-free).
/// Compares coordinator overhead against the raw arena+refcount operations measured by other benchmarks.
///
/// The buffer and single-element array are created once in setup and reused via <see cref="ThreadLocalBuffer{T}.Clear"/>
/// across all N iterations — matching the production pattern where each worker holds a persistent buffer.
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
public class CoordinatorBenchmarks
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TreeNode : IArenaNode
    {
        public int Left;
        public int Right;

        public void FixupReferences(ReadOnlySpan<int> localToGlobalMap)
        {
            if (ArenaIndex.IsLocal(this.Left))
                this.Left = localToGlobalMap[ArenaIndex.UntagLocal(this.Left)];
            if (ArenaIndex.IsLocal(this.Right))
                this.Right = localToGlobalMap[ArenaIndex.UntagLocal(this.Right)];
        }

        public void IncrementChildren(RefCountTable table)
        {
            if (this.Left != ArenaIndex.NoChild)
                table.Increment(this.Left);
            if (this.Right != ArenaIndex.NoChild)
                table.Increment(this.Right);
        }
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

    // ── Parameters ───────────────────────────────────────────────────────

    [Params(19)]
    public int TreeDepth;

    [Params(1_000)]
    public int N;

    // ── State ────────────────────────────────────────────────────────────

    private UnsafeSlabArena<TreeNode> _arena = null!;
    private RefCountTable _refCounts = null!;
    private ArenaCoordinator<TreeNode> _coordinator = null!;
    private ThreadLocalBuffer<TreeNode> _buffer = null!;
    private ThreadLocalBuffer<TreeNode>[] _bufferArray = null!;
    private Random _rng = null!;
    private int _rootIndex;

    // ── Setup ────────────────────────────────────────────────────────────

    [IterationSetup]
    public void Setup()
    {
        var nodeCount = (1 << (TreeDepth + 1)) - 1;
        _arena = new UnsafeSlabArena<TreeNode>();
        _refCounts = new RefCountTable();
        _coordinator = new ArenaCoordinator<TreeNode>(_arena, _refCounts);
        _buffer = new ThreadLocalBuffer<TreeNode>();
        _bufferArray = [_buffer];

        var maxCapacity = nodeCount + (N * (TreeDepth + 1)) + 1024;
        _refCounts.EnsureCapacity(maxCapacity);

        for (var i = 0; i < nodeCount; i++)
            _arena.Allocate();

        for (var i = 0; i < nodeCount; i++)
        {
            var left = (2 * i) + 1;
            var right = (2 * i) + 2;
            _arena[i] = new TreeNode
            {
                Left = left < nodeCount ? left : -1,
                Right = right < nodeCount ? right : -1
            };
        }

        _refCounts.Increment(0);
        for (var i = 0; i < nodeCount; i++)
        {
            ref readonly var node = ref _arena[i];
            if (node.Left >= 0) _refCounts.Increment(node.Left);
            if (node.Right >= 0) _refCounts.Increment(node.Right);
        }

        _rng = new Random(42);
        _rootIndex = 0;
    }

    // ── Path-copy into reusable buffer ───────────────────────────────────

    private int FillBuffer(int currentRoot, int leafAddress)
    {
        _buffer.Clear();

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

        var newLeaf = _buffer.Append(new TreeNode { Left = -1, Right = -1 });

        var childOnPath = newLeaf;
        for (var level = TreeDepth - 1; level >= 0; level--)
        {
            ref readonly var orig = ref _arena[pathNodes[level]];
            int left, right;
            if (wentLeft[level])
            {
                left = ArenaIndex.TagLocal(childOnPath);
                right = orig.Right;
            }
            else
            {
                left = orig.Left;
                right = ArenaIndex.TagLocal(childOnPath);
            }

            childOnPath = _buffer.Append(new TreeNode { Left = left, Right = right });
        }

        return childOnPath;
    }

    // ═══════════════════════ Coordinator benchmarks ═══════════════════════

    /// <summary>
    /// Full coordinator pipeline: N fork-then-release cycles using a single reusable buffer. Each cycle clears
    /// the buffer, fills it with a path-copy spine, merges, increments the new root, then releases the old root.
    /// Divide Mean by N for per-change cost.
    /// </summary>
    [Benchmark(Baseline = true)]
    public int Coordinator_ForkThenRelease()
    {
        var leafCount = 1 << TreeDepth;
        var handler = new TreeHandler(_arena);
        var root = _rootIndex;

        for (var i = 0; i < N; i++)
        {
            var localRoot = FillBuffer(root, _rng.Next(leafCount));

            _coordinator.MergeBuffer(_buffer);
            var newRoot = _coordinator.GetGlobalIndex(localRoot);
            _refCounts.Increment(newRoot);

            _buffer.LogRelease(root);
            _coordinator.ProcessReleases(_bufferArray, handler);

            root = newRoot;
        }

        return root;
    }

    /// <summary>
    /// Buffer merge only (no releases): N fork cycles using a single reusable buffer. Measures the pure merge
    /// pipeline cost. Divide Mean by N for per-merge cost.
    /// </summary>
    [Benchmark]
    public int Coordinator_MergeOnly()
    {
        var leafCount = 1 << TreeDepth;
        var root = _rootIndex;

        for (var i = 0; i < N; i++)
        {
            var localRoot = FillBuffer(root, _rng.Next(leafCount));

            _coordinator.MergeBuffer(_buffer);
            var newRoot = _coordinator.GetGlobalIndex(localRoot);
            _refCounts.Increment(newRoot);

            root = newRoot;
        }

        return root;
    }

    /// <summary>
    /// Raw buffer fill throughput: N fill cycles using a single reusable buffer (no merge, no coordinator).
    /// Isolates the cost of the ThreadLocalBuffer append path. Divide Mean by N for per-fill cost.
    /// </summary>
    [Benchmark]
    public int BufferFill_Only()
    {
        var leafCount = 1 << TreeDepth;
        var root = _rootIndex;
        var lastLocal = 0;

        for (var i = 0; i < N; i++)
        {
            lastLocal = FillBuffer(root, _rng.Next(leafCount));
        }

        return lastLocal;
    }
}
