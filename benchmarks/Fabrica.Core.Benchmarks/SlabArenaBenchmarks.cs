using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Fabrica.Core.Memory;

namespace Fabrica.Core.Benchmarks;

/// <summary>
/// Benchmarks for <see cref="UnsafeSlabArena{T}"/> measuring core operations, access patterns, and steady-state workloads
/// at production-default parameters (real directory size, real LOH-aware slab lengths).
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
public class UnsafeSlabArenaBenchmarks
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
        var arena = new UnsafeSlabArena<Small>();
        var last = Handle<Small>.None;
        for (var i = 0; i < N; i++)
            last = arena.Allocate();
        return last.Index;
    }

    [Benchmark]
    public int Allocate_Medium()
    {
        var arena = new UnsafeSlabArena<Medium>();
        var last = Handle<Medium>.None;
        for (var i = 0; i < N; i++)
            last = arena.Allocate();
        return last.Index;
    }

    [Benchmark]
    public int Allocate_Large()
    {
        var arena = new UnsafeSlabArena<Large>();
        var last = Handle<Large>.None;
        for (var i = 0; i < N; i++)
            last = arena.Allocate();
        return last.Index;
    }

    [Benchmark]
    public int AllocateAndFree_Small()
    {
        var arena = new UnsafeSlabArena<Small>();
        for (var i = 0; i < N; i++)
            arena.Allocate();
        for (var i = N - 1; i >= 0; i--)
            arena.Free(new Handle<Small>(i));
        return arena.Allocate().Index;
    }

    // ═══════════════════════════ Free-list reuse ═════════════════════════

    [Benchmark]
    public int Allocate_FromFreeList()
    {
        var arena = new UnsafeSlabArena<Medium>();
        for (var i = 0; i < N; i++)
            arena.Allocate();
        for (var i = N - 1; i >= 0; i--)
            arena.Free(new Handle<Medium>(i));

        var last = Handle<Medium>.None;
        for (var i = 0; i < N; i++)
            last = arena.Allocate();
        return last.Index;
    }

    [Benchmark]
    public int Allocate_FromFreeList_RandomOrder()
    {
        var arena = new UnsafeSlabArena<Medium>();
        var handles = new Handle<Medium>[N];
        for (var i = 0; i < N; i++)
            handles[i] = arena.Allocate();

        var rng = new Random(42);
        rng.Shuffle(handles);
        for (var i = 0; i < N; i++)
            arena.Free(handles[i]);

        var last = Handle<Medium>.None;
        for (var i = 0; i < N; i++)
            last = arena.Allocate();
        return last.Index;
    }

    // ═══════════════════════════ Indexed access ═══════════════════════════

    private UnsafeSlabArena<Medium>? _readArena;
    private Handle<Medium>[]? _sequentialIndices;
    private Handle<Medium>[]? _randomIndices;

    [GlobalSetup(Targets = [nameof(Read_Sequential), nameof(Read_Random)])]
    public void SetupReads()
    {
        _readArena = new UnsafeSlabArena<Medium>();
        _sequentialIndices = new Handle<Medium>[N];
        for (var i = 0; i < N; i++)
        {
            _sequentialIndices[i] = _readArena.Allocate();
            _readArena[_sequentialIndices[i]] = new Medium { Value = i };
        }

        _randomIndices = (Handle<Medium>[])_sequentialIndices.Clone();
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
        var arena = new UnsafeSlabArena<Medium>();

        var half = N / 2;
        for (var i = 0; i < half; i++)
            arena.Allocate();

        var last = Handle<Medium>.None;
        for (var i = 0; i < N; i++)
        {
            last = arena.Allocate();
            arena[last] = new Medium { Value = i };
            arena.Free(last);
        }

        return last.Index;
    }

    [Benchmark]
    public int SteadyState_BulkAllocThenBulkFree()
    {
        var arena = new UnsafeSlabArena<Medium>();
        var batchSize = Math.Max(N / 10, 1);
        var batchHandles = new Handle<Medium>[batchSize];
        var last = Handle<Medium>.None;

        for (var batch = 0; batch < 10; batch++)
        {
            for (var i = 0; i < batchSize; i++)
            {
                batchHandles[i] = arena.Allocate();
                arena[batchHandles[i]] = new Medium { Value = i };
                last = batchHandles[i];
            }

            for (var i = 0; i < batchSize; i++)
                arena.Free(batchHandles[i]);
        }

        return last.Index;
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
