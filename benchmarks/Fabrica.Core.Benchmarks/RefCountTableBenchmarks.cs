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

    // ── Struct helpers (zero interface dispatch overhead) ─────────────────

    private struct NullEvents : RefCountTable.IRefCountEvents
    {
        public void OnFreed(int index) { }
    }

    private struct CountingEvents : RefCountTable.IRefCountEvents
    {
        public int FreeCount;
        public void OnFreed(int index) => this.FreeCount++;
    }

    private struct NoChildren : RefCountTable.IChildEnumerator
    {
        public void EnumerateChildren(int index, RefCountTable table) { }
    }

    private struct BinaryTreeChildren(int maxIndex) : RefCountTable.IChildEnumerator
    {
        public void EnumerateChildren(int index, RefCountTable table)
        {
            var left = (index * 2) + 1;
            var right = (index * 2) + 2;
            if (left <= maxIndex)
                table.DecrementChild(left);
            if (right <= maxIndex)
                table.DecrementChild(right);
        }
    }

    private struct LinearChainChildren(int maxIndex) : RefCountTable.IChildEnumerator
    {
        public void EnumerateChildren(int index, RefCountTable table)
        {
            var next = index + 1;
            if (next <= maxIndex)
                table.DecrementChild(next);
        }
    }

    private struct WideTreeChildren(int fanout) : RefCountTable.IChildEnumerator
    {
        public void EnumerateChildren(int index, RefCountTable table)
        {
            if (index != 0) return;
            for (var i = 1; i <= fanout; i++)
                table.DecrementChild(i);
        }
    }

    // ═══════════════════════════ Increment ════════════════════════════════

    [Benchmark(Baseline = true)]
    public void Increment_Sequential()
    {
        var table = new RefCountTable();
        table.EnsureCapacity(N);
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
        table.EnsureCapacity(N);
        var indices = _randomIndices!;
        for (var i = 0; i < indices.Length; i++)
            table.Increment(indices[i]);
    }

    // ═══════════════════════════ Decrement ════════════════════════════════

    [Benchmark]
    public void Decrement_NoFrees()
    {
        var table = new RefCountTable();
        table.EnsureCapacity(N);
        for (var i = 0; i < N; i++)
        {
            table.Increment(i);
            table.Increment(i); // rc = 2
        }

        for (var i = 0; i < N; i++)
            table.Decrement(i, default(NullEvents), default(NoChildren)); // rc 2→1, no frees
    }

    [Benchmark]
    public int Decrement_AllFree()
    {
        var table = new RefCountTable();
        table.EnsureCapacity(N);
        var events = new CountingEvents();
        for (var i = 0; i < N; i++)
            table.Increment(i);

        for (var i = 0; i < N; i++)
            table.Decrement(i, events, default(NoChildren));

        return events.FreeCount;
    }

    // ═══════════════════════════ Cascade ══════════════════════════════════

    [Benchmark]
    public int CascadeDecrement_BinaryTree()
    {
        var table = new RefCountTable();
        table.EnsureCapacity(N);
        var events = new CountingEvents();
        for (var i = 0; i < N; i++)
            table.Increment(i);

        table.Decrement(0, events, new BinaryTreeChildren(N - 1));
        return events.FreeCount;
    }

    [Benchmark]
    public int CascadeDecrement_LinearChain()
    {
        var table = new RefCountTable();
        table.EnsureCapacity(N);
        var events = new CountingEvents();
        for (var i = 0; i < N; i++)
            table.Increment(i);

        table.Decrement(0, events, new LinearChainChildren(N - 1));
        return events.FreeCount;
    }

    [Benchmark]
    public int CascadeDecrement_WideTree()
    {
        var table = new RefCountTable();
        table.EnsureCapacity(N);
        var events = new CountingEvents();
        for (var i = 0; i < N; i++)
            table.Increment(i);

        table.Decrement(0, events, new WideTreeChildren(N - 1));
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

        table.EnsureCapacity(N);
        table.IncrementBatch(indices);
    }

    [Benchmark]
    public int DecrementBatch_Mixed()
    {
        var table = new RefCountTable();
        table.EnsureCapacity(N);
        var events = new CountingEvents();
        var half = N / 2;

        for (var i = 0; i < N; i++)
        {
            table.Increment(i);
            if (i < half)
                table.Increment(i);
        }

        var indices = new int[N];
        for (var i = 0; i < N; i++)
            indices[i] = i;

        table.DecrementBatch(indices, events, default(NoChildren));
        return events.FreeCount;
    }

    // ═══════════════════════════ Steady state ═════════════════════════════

    [Benchmark]
    public void SteadyState_IncrementDecrement()
    {
        var table = new RefCountTable();
        table.EnsureCapacity(N);

        for (var i = 0; i < N; i++)
        {
            table.Increment(i);
            table.Increment(i);
        }

        for (var i = 0; i < N; i++)
        {
            table.Decrement(i, default(NullEvents), default(NoChildren));
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
