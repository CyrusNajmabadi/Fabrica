using BenchmarkDotNet.Attributes;
using Fabrica.Core.Memory;

namespace Fabrica.Core.Benchmarks;

/// <summary>
/// Benchmarks for <see cref="RefCountTable{T}"/> measuring increment/decrement throughput, cascade-free patterns
/// across different tree shapes, batch operations, and steady-state workloads at production-default parameters.
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
public class RefCountTableBenchmarks
{
    [Params(1_000, 10_000, 100_000)]
    public int N;

    private struct DummyNode;

    // ── Struct helpers (zero interface dispatch overhead) ─────────────────

    private struct NullHandler : RefCountTable<DummyNode>.IRefCountHandler
    {
        public void OnFreed(Handle<DummyNode> handle, RefCountTable<DummyNode> table) { }
    }

    private struct CountingHandler : RefCountTable<DummyNode>.IRefCountHandler
    {
        public int FreeCount;
        public void OnFreed(Handle<DummyNode> handle, RefCountTable<DummyNode> table) => this.FreeCount++;
    }

    private struct BinaryTreeHandler(int maxIndex) : RefCountTable<DummyNode>.IRefCountHandler
    {
        public int FreeCount;

        public void OnFreed(Handle<DummyNode> handle, RefCountTable<DummyNode> table)
        {
            this.FreeCount++;
            var index = handle.Index;
            var left = (index * 2) + 1;
            var right = (index * 2) + 2;
            if (left <= maxIndex)
                table.Decrement(new Handle<DummyNode>(left), this);
            if (right <= maxIndex)
                table.Decrement(new Handle<DummyNode>(right), this);
        }
    }

    private struct LinearChainHandler(int maxIndex) : RefCountTable<DummyNode>.IRefCountHandler
    {
        public int FreeCount;

        public void OnFreed(Handle<DummyNode> handle, RefCountTable<DummyNode> table)
        {
            this.FreeCount++;
            var next = handle.Index + 1;
            if (next <= maxIndex)
                table.Decrement(new Handle<DummyNode>(next), this);
        }
    }

    private struct WideTreeHandler(int fanout) : RefCountTable<DummyNode>.IRefCountHandler
    {
        public int FreeCount;

        public void OnFreed(Handle<DummyNode> handle, RefCountTable<DummyNode> table)
        {
            this.FreeCount++;
            if (handle.Index != 0) return;
            for (var i = 1; i <= fanout; i++)
                table.Decrement(new Handle<DummyNode>(i), this);
        }
    }

    // ═══════════════════════════ Increment ════════════════════════════════

    [Benchmark(Baseline = true)]
    public void Increment_Sequential()
    {
        var table = new RefCountTable<DummyNode>();
        table.EnsureCapacity(N);
        for (var i = 0; i < N; i++)
            table.Increment(new Handle<DummyNode>(i));
    }

    private int[]? _randomIndices;

    [GlobalSetup(Target = nameof(Increment_Random))]
    public void SetupRandomIndices()
    {
        _randomIndices = new int[N];
        for (var i = 0; i < N; i++)
            _randomIndices[i] = i;
        var rng = new Random(42);
        rng.Shuffle(_randomIndices);
    }

    [Benchmark]
    public void Increment_Random()
    {
        var table = new RefCountTable<DummyNode>();
        table.EnsureCapacity(N);
        var indices = _randomIndices!;
        for (var i = 0; i < indices.Length; i++)
            table.Increment(new Handle<DummyNode>(indices[i]));
    }

    // ═══════════════════════════ Decrement ════════════════════════════════

    [Benchmark]
    public void Decrement_NoFrees()
    {
        var table = new RefCountTable<DummyNode>();
        table.EnsureCapacity(N);
        for (var i = 0; i < N; i++)
        {
            table.Increment(new Handle<DummyNode>(i));
            table.Increment(new Handle<DummyNode>(i));
        }

        for (var i = 0; i < N; i++)
            table.Decrement(new Handle<DummyNode>(i), default(NullHandler));
    }

    [Benchmark]
    public int Decrement_AllFree()
    {
        var table = new RefCountTable<DummyNode>();
        table.EnsureCapacity(N);
        var handler = new CountingHandler();
        for (var i = 0; i < N; i++)
            table.Increment(new Handle<DummyNode>(i));

        for (var i = 0; i < N; i++)
            table.Decrement(new Handle<DummyNode>(i), handler);

        return handler.FreeCount;
    }

    // ═══════════════════════════ Cascade ══════════════════════════════════

    [Benchmark]
    public int CascadeDecrement_BinaryTree()
    {
        var table = new RefCountTable<DummyNode>();
        table.EnsureCapacity(N);
        var handler = new BinaryTreeHandler(N - 1);
        for (var i = 0; i < N; i++)
            table.Increment(new Handle<DummyNode>(i));

        table.Decrement(new Handle<DummyNode>(0), handler);
        return handler.FreeCount;
    }

    [Benchmark]
    public int CascadeDecrement_LinearChain()
    {
        var table = new RefCountTable<DummyNode>();
        table.EnsureCapacity(N);
        var handler = new LinearChainHandler(N - 1);
        for (var i = 0; i < N; i++)
            table.Increment(new Handle<DummyNode>(i));

        table.Decrement(new Handle<DummyNode>(0), handler);
        return handler.FreeCount;
    }

    [Benchmark]
    public int CascadeDecrement_WideTree()
    {
        var table = new RefCountTable<DummyNode>();
        table.EnsureCapacity(N);
        var handler = new WideTreeHandler(N - 1);
        for (var i = 0; i < N; i++)
            table.Increment(new Handle<DummyNode>(i));

        table.Decrement(new Handle<DummyNode>(0), handler);
        return handler.FreeCount;
    }

    // ═══════════════════════════ Batch ════════════════════════════════════

    [Benchmark]
    public void IncrementBatch()
    {
        var table = new RefCountTable<DummyNode>();
        var handles = new Handle<DummyNode>[N];
        for (var i = 0; i < N; i++)
            handles[i] = new Handle<DummyNode>(i);

        table.EnsureCapacity(N);
        table.IncrementBatch(handles);
    }

    [Benchmark]
    public int DecrementBatch_Mixed()
    {
        var table = new RefCountTable<DummyNode>();
        table.EnsureCapacity(N);
        var handler = new CountingHandler();
        var half = N / 2;

        for (var i = 0; i < N; i++)
        {
            table.Increment(new Handle<DummyNode>(i));
            if (i < half)
                table.Increment(new Handle<DummyNode>(i));
        }

        var handles = new Handle<DummyNode>[N];
        for (var i = 0; i < N; i++)
            handles[i] = new Handle<DummyNode>(i);

        table.DecrementBatch(handles, handler);
        return handler.FreeCount;
    }

    // ═══════════════════════════ Steady state ═════════════════════════════

    [Benchmark]
    public void SteadyState_IncrementDecrement()
    {
        var table = new RefCountTable<DummyNode>();
        table.EnsureCapacity(N);

        for (var i = 0; i < N; i++)
        {
            table.Increment(new Handle<DummyNode>(i));
            table.Increment(new Handle<DummyNode>(i));
        }

        for (var i = 0; i < N; i++)
        {
            table.Decrement(new Handle<DummyNode>(i), default(NullHandler));
            table.Increment(new Handle<DummyNode>(i));
        }
    }

    // ═══════════════════════════ Baseline ═════════════════════════════════

    [Benchmark]
    public void Baseline_FlatArray_Increment()
    {
        var array = new int[N];
        for (var i = 0; i < N; i++)
            array[i]++;
    }
}
