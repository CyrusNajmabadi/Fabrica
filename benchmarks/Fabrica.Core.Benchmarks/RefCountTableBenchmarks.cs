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
            _ = table.Decrement(new Handle<DummyNode>(i));
    }

    [Benchmark]
    public int Decrement_AllFree()
    {
        var table = new RefCountTable<DummyNode>();
        table.EnsureCapacity(N);
        for (var i = 0; i < N; i++)
            table.Increment(new Handle<DummyNode>(i));

        var count = 0;
        for (var i = 0; i < N; i++)
            if (table.Decrement(new Handle<DummyNode>(i)))
                count++;

        return count;
    }

    // ═══════════════════════════ Cascade ══════════════════════════════════

    [Benchmark]
    public int CascadeDecrement_BinaryTree()
    {
        var table = new RefCountTable<DummyNode>();
        table.EnsureCapacity(N);
        for (var i = 0; i < N; i++)
            table.Increment(new Handle<DummyNode>(i));

        var pending = new Stack<Handle<DummyNode>>();
        var maxIndex = N - 1;
        var freeCount = 0;
        if (table.Decrement(new Handle<DummyNode>(0)))
            pending.Push(new Handle<DummyNode>(0));
        while (pending.TryPop(out var current))
        {
            freeCount++;
            var left = (current.Index * 2) + 1;
            var right = (current.Index * 2) + 2;
            if (left <= maxIndex && table.Decrement(new Handle<DummyNode>(left)))
                pending.Push(new Handle<DummyNode>(left));
            if (right <= maxIndex && table.Decrement(new Handle<DummyNode>(right)))
                pending.Push(new Handle<DummyNode>(right));
        }

        return freeCount;
    }

    [Benchmark]
    public int CascadeDecrement_LinearChain()
    {
        var table = new RefCountTable<DummyNode>();
        table.EnsureCapacity(N);
        for (var i = 0; i < N; i++)
            table.Increment(new Handle<DummyNode>(i));

        var pending = new Stack<Handle<DummyNode>>();
        var maxIndex = N - 1;
        var freeCount = 0;
        if (table.Decrement(new Handle<DummyNode>(0)))
            pending.Push(new Handle<DummyNode>(0));
        while (pending.TryPop(out var current))
        {
            freeCount++;
            var next = current.Index + 1;
            if (next <= maxIndex && table.Decrement(new Handle<DummyNode>(next)))
                pending.Push(new Handle<DummyNode>(next));
        }

        return freeCount;
    }

    [Benchmark]
    public int CascadeDecrement_WideTree()
    {
        var table = new RefCountTable<DummyNode>();
        table.EnsureCapacity(N);
        for (var i = 0; i < N; i++)
            table.Increment(new Handle<DummyNode>(i));

        var pending = new Stack<Handle<DummyNode>>();
        var fanout = N - 1;
        var freeCount = 0;
        if (table.Decrement(new Handle<DummyNode>(0)))
            pending.Push(new Handle<DummyNode>(0));
        while (pending.TryPop(out var current))
        {
            freeCount++;
            if (current.Index == 0)
            {
                for (var i = 1; i <= fanout; i++)
                    if (table.Decrement(new Handle<DummyNode>(i)))
                        pending.Push(new Handle<DummyNode>(i));
            }
        }

        return freeCount;
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

        var hitZero = new UnsafeStack<Handle<DummyNode>>();
        table.DecrementBatch(handles, hitZero);
        return hitZero.Count;
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
            _ = table.Decrement(new Handle<DummyNode>(i));
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
