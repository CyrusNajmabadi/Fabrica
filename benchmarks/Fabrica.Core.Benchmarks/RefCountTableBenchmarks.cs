using BenchmarkDotNet.Attributes;
using Fabrica.Core.Memory;

namespace Fabrica.Core.Benchmarks;

/// <summary>
/// Benchmarks for <see cref="RefCountTable"/> measuring increment/decrement throughput, cascade-free patterns
/// across different tree shapes, batch operations, and steady-state workloads at production-default parameters.
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
public class RefCountTableBenchmarks
{
    [Params(1_000, 10_000, 100_000)]
    public int N;

    // ── Shared helpers ───────────────────────────────────────────────────

    private sealed class NullEvents : RefCountTable.IRefCountEvents
    {
        public static readonly NullEvents Instance = new();
        public void OnFreed(int index) { }
    }

    private sealed class CountingEvents : RefCountTable.IRefCountEvents
    {
        public int FreeCount;
        public void OnFreed(int index) => this.FreeCount++;
    }

    private sealed class BinaryTreeChildren(int maxIndex) : RefCountTable.IChildEnumerator
    {
        public void EnumerateChildren(int index, ref UnsafeStack<int> worklist, RefCountTable table)
        {
            var left = (index * 2) + 1;
            var right = (index * 2) + 2;
            if (left <= maxIndex)
                table.DecrementChild(left, ref worklist);
            if (right <= maxIndex)
                table.DecrementChild(right, ref worklist);
        }
    }

    private sealed class LinearChainChildren(int maxIndex) : RefCountTable.IChildEnumerator
    {
        public void EnumerateChildren(int index, ref UnsafeStack<int> worklist, RefCountTable table)
        {
            var next = index + 1;
            if (next <= maxIndex)
                table.DecrementChild(next, ref worklist);
        }
    }

    private sealed class WideTreeChildren(int fanout) : RefCountTable.IChildEnumerator
    {
        public void EnumerateChildren(int index, ref UnsafeStack<int> worklist, RefCountTable table)
        {
            if (index != 0) return;
            for (var i = 1; i <= fanout; i++)
                table.DecrementChild(i, ref worklist);
        }
    }

    // ═══════════════════════════ Increment ════════════════════════════════

    [Benchmark(Baseline = true)]
    public void Increment_Sequential()
    {
        var table = new RefCountTable();
        for (var i = 0; i < N; i++)
            table.Increment(i);
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
        var table = new RefCountTable();
        var indices = _randomIndices!;
        for (var i = 0; i < indices.Length; i++)
            table.Increment(indices[i]);
    }

    // ═══════════════════════════ Decrement ════════════════════════════════

    [Benchmark]
    public void Decrement_NoFrees()
    {
        var table = new RefCountTable();
        for (var i = 0; i < N; i++)
        {
            table.Increment(i);
            table.Increment(i); // rc = 2
        }

        for (var i = 0; i < N; i++)
            table.Decrement(i, NullEvents.Instance); // rc 2→1, no frees
    }

    [Benchmark]
    public int Decrement_AllFree()
    {
        var table = new RefCountTable();
        var events = new CountingEvents();
        for (var i = 0; i < N; i++)
            table.Increment(i); // rc = 1

        for (var i = 0; i < N; i++)
            table.Decrement(i, events); // rc 1→0, all free

        return events.FreeCount;
    }

    // ═══════════════════════════ Cascade ══════════════════════════════════

    [Benchmark]
    public int CascadeDecrement_BinaryTree()
    {
        // Build a complete binary tree of size N in index layout: children of i are 2i+1, 2i+2
        var table = new RefCountTable();
        var events = new CountingEvents();
        for (var i = 0; i < N; i++)
            table.Increment(i);

        table.DecrementCascade(0, events, new BinaryTreeChildren(N - 1));
        return events.FreeCount;
    }

    [Benchmark]
    public int CascadeDecrement_LinearChain()
    {
        var table = new RefCountTable();
        var events = new CountingEvents();
        for (var i = 0; i < N; i++)
            table.Increment(i);

        table.DecrementCascade(0, events, new LinearChainChildren(N - 1));
        return events.FreeCount;
    }

    [Benchmark]
    public int CascadeDecrement_WideTree()
    {
        // Root (index 0) points to N-1 children (indices 1..N-1)
        var table = new RefCountTable();
        var events = new CountingEvents();
        for (var i = 0; i < N; i++)
            table.Increment(i);

        table.DecrementCascade(0, events, new WideTreeChildren(N - 1));
        return events.FreeCount;
    }

    // ═══════════════════════════ Batch ════════════════════════════════════

    [Benchmark]
    public void IncrementBatch()
    {
        var table = new RefCountTable();
        var indices = new int[N];
        for (var i = 0; i < N; i++)
            indices[i] = i;

        table.IncrementBatch(indices);
    }

    [Benchmark]
    public int DecrementBatch_Mixed()
    {
        // Half at rc=2 (won't free), half at rc=1 (will free)
        var table = new RefCountTable();
        var events = new CountingEvents();
        var half = N / 2;

        for (var i = 0; i < N; i++)
        {
            table.Increment(i);
            if (i < half)
                table.Increment(i); // rc = 2 for first half
        }

        var indices = new int[N];
        for (var i = 0; i < N; i++)
            indices[i] = i;

        table.DecrementBatch(indices, events);
        return events.FreeCount;
    }

    // ═══════════════════════════ Steady state ═════════════════════════════

    [Benchmark]
    public void SteadyState_IncrementDecrement()
    {
        var table = new RefCountTable();

        // Warm up: increment all to rc=2
        for (var i = 0; i < N; i++)
        {
            table.Increment(i);
            table.Increment(i);
        }

        // Steady state: decrement then re-increment (rc oscillates 2→1→2)
        for (var i = 0; i < N; i++)
        {
            table.Decrement(i, NullEvents.Instance);
            table.Increment(i);
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
