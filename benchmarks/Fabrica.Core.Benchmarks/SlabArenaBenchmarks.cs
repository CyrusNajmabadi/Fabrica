using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Fabrica.Core.Memory;

namespace Fabrica.Core.Benchmarks;

/// <summary>
/// Benchmarks for <see cref="SlabArena{T}"/> measuring core operations, access patterns, and steady-state workloads
/// at production-default parameters (real directory size, real LOH-aware slab lengths).
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
public class SlabArenaBenchmarks
{
    // ── Payload types (varying sizes to exercise different slab lengths) ──

    private struct Small // 8 bytes
    {
        public long Value { get; set; }
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct Medium // 64 bytes — typical tree node
    {
        public long Value { get; set; }
    }

    [StructLayout(LayoutKind.Sequential, Size = 4096)]
    private struct Large // 4KB — worst-case large struct
    {
        public long Value { get; set; }
    }

    // ── Parameters ───────────────────────────────────────────────────────

    [Params(1_000, 10_000, 100_000)]
    public int N;

    // ═══════════════════════════ Core operations ══════════════════════════

    [Benchmark(Baseline = true)]
    public int Allocate_Small()
    {
        var arena = new SlabArena<Small>();
        var last = 0;
        for (var i = 0; i < N; i++)
            last = arena.Allocate();
        return last;
    }

    [Benchmark]
    public int Allocate_Medium()
    {
        var arena = new SlabArena<Medium>();
        var last = 0;
        for (var i = 0; i < N; i++)
            last = arena.Allocate();
        return last;
    }

    [Benchmark]
    public int Allocate_Large()
    {
        var arena = new SlabArena<Large>();
        var last = 0;
        for (var i = 0; i < N; i++)
            last = arena.Allocate();
        return last;
    }

    [Benchmark]
    public int AllocateAndFree_Small()
    {
        var arena = new SlabArena<Small>();
        for (var i = 0; i < N; i++)
            arena.Allocate();
        for (var i = N - 1; i >= 0; i--)
            arena.Free(i);
        return arena.Allocate();
    }

    // ═══════════════════════════ Free-list reuse ═════════════════════════

    [Benchmark]
    public int Allocate_FromFreeList()
    {
        var arena = new SlabArena<Medium>();
        for (var i = 0; i < N; i++)
            arena.Allocate();
        for (var i = N - 1; i >= 0; i--)
            arena.Free(i);

        var last = 0;
        for (var i = 0; i < N; i++)
            last = arena.Allocate();
        return last;
    }

    [Benchmark]
    public int Allocate_FromFreeList_RandomOrder()
    {
        var arena = new SlabArena<Medium>();
        var indices = new int[N];
        for (var i = 0; i < N; i++)
            indices[i] = arena.Allocate();

        var rng = new Random(42);
        rng.Shuffle(indices);
        for (var i = 0; i < N; i++)
            arena.Free(indices[i]);

        var last = 0;
        for (var i = 0; i < N; i++)
            last = arena.Allocate();
        return last;
    }

    // ═══════════════════════════ Indexed access ═══════════════════════════

    private SlabArena<Medium>? _readArena;
    private int[]? _sequentialIndices;
    private int[]? _randomIndices;

    [GlobalSetup(Targets = [nameof(Read_Sequential), nameof(Read_Random)])]
    public void SetupReads()
    {
        _readArena = new SlabArena<Medium>();
        _sequentialIndices = new int[N];
        for (var i = 0; i < N; i++)
        {
            _sequentialIndices[i] = _readArena.Allocate();
            _readArena[_sequentialIndices[i]] = new Medium { Value = i };
        }

        _randomIndices = (int[])_sequentialIndices.Clone();
        var rng = new Random(42);
        rng.Shuffle(_randomIndices);
    }

    [Benchmark]
    public long Read_Sequential()
    {
        var arena = _readArena!;
        var indices = _sequentialIndices!;
        long sum = 0;
        for (var i = 0; i < indices.Length; i++)
            sum += arena[indices[i]].Value;
        return sum;
    }

    [Benchmark]
    public long Read_Random()
    {
        var arena = _readArena!;
        var indices = _randomIndices!;
        long sum = 0;
        for (var i = 0; i < indices.Length; i++)
            sum += arena[indices[i]].Value;
        return sum;
    }

    // ═══════════════════════════ Steady-state patterns ════════════════════

    [Benchmark]
    public int SteadyState_Interleaved()
    {
        var arena = new SlabArena<Medium>();

        // Warm up: fill half
        var half = N / 2;
        for (var i = 0; i < half; i++)
            arena.Allocate();

        // Steady state: allocate one, free one, repeatedly
        var last = 0;
        for (var i = 0; i < N; i++)
        {
            last = arena.Allocate();
            arena[last] = new Medium { Value = i };
            arena.Free(last);
        }

        return last;
    }

    [Benchmark]
    public int SteadyState_BulkAllocThenBulkFree()
    {
        var arena = new SlabArena<Medium>();
        var batchSize = Math.Max(N / 10, 1);
        var batchIndices = new int[batchSize];
        var last = 0;

        for (var batch = 0; batch < 10; batch++)
        {
            for (var i = 0; i < batchSize; i++)
            {
                batchIndices[i] = arena.Allocate();
                arena[batchIndices[i]] = new Medium { Value = i };
                last = batchIndices[i];
            }

            for (var i = 0; i < batchSize; i++)
                arena.Free(batchIndices[i]);
        }

        return last;
    }

    // ═══════════════════════════ Baselines (comparison) ═══════════════════

    [Benchmark]
    public int Baseline_FlatArray()
    {
        var array = new Medium[N];
        for (var i = 0; i < N; i++)
            array[i] = new Medium { Value = i };
        return array.Length;
    }

    [Benchmark]
    public long Baseline_FlatArray_Read_Sequential()
    {
        var array = new Medium[N];
        for (var i = 0; i < N; i++)
            array[i] = new Medium { Value = i };
        long sum = 0;
        for (var i = 0; i < N; i++)
            sum += array[i].Value;
        return sum;
    }
}
