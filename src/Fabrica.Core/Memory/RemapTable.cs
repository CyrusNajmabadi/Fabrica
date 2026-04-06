using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Fabrica.Core.Memory;

/// <summary>
/// Maps local buffer indices to global arena indices for a single node type during the coordinator
/// merge. Each worker thread has its own mapping array; local index <c>i</c> on thread <c>t</c>
/// maps to global index <c>Resolve(t, i)</c>.
///
/// STORAGE
///   <see cref="UnsafeList{T}"/>[] indexed by thread ID. The outer array is fixed-size (worker
///   count, known at construction). Each inner list grows as mappings are added and retains its
///   backing array across <see cref="Reset"/> calls for zero steady-state allocation.
///
/// NON-GENERIC
///   The mapping is purely <c>(threadId, localIndex) -> globalIndex</c>, independent of node type.
///   This allows a single pool of <see cref="RemapTable"/> instances shared across all types and
///   ticks. The coordinator assigns one instance per node type during each merge pass.
///
/// THREAD MODEL
///   Single-threaded. Built by the coordinator (or a per-type merge worker) during Phase 1, read
///   during Phase 2a. No synchronization.
/// </summary>
public sealed class RemapTable
{
    private readonly UnsafeList<int>[] _remap;

    public RemapTable(int threadCount)
    {
        Debug.Assert(threadCount > 0, $"RemapTable requires at least 1 thread, got {threadCount}.");
        _remap = new UnsafeList<int>[threadCount];
        for (var i = 0; i < threadCount; i++)
            _remap[i] = new UnsafeList<int>();
    }

    /// <summary>Number of threads this remap table supports.</summary>
    public int ThreadCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _remap.Length;
    }

    /// <summary>Number of mappings recorded for the given thread.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Count(int threadId) => _remap[threadId].Count;

    /// <summary>
    /// Records that local index <paramref name="localIndex"/> on thread <paramref name="threadId"/>
    /// maps to <paramref name="globalIndex"/>. Mappings must be added in order (local index 0, 1, 2, ...)
    /// for each thread.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetMapping(int threadId, int localIndex, int globalIndex)
    {
        Debug.Assert(localIndex == _remap[threadId].Count,
            $"SetMapping out of order: expected localIndex {_remap[threadId].Count}, got {localIndex}.");
        _remap[threadId].Add(globalIndex);
    }

    /// <summary>
    /// Returns the global index for the given <paramref name="threadId"/> and <paramref name="localIndex"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Resolve(int threadId, int localIndex) => _remap[threadId][localIndex];

    /// <summary>
    /// Resets all per-thread mapping lists to empty. The backing arrays are retained so the next
    /// merge pass incurs no allocation in steady state.
    /// </summary>
    public void Reset()
    {
        for (var i = 0; i < _remap.Length; i++)
            _remap[i].Reset();
    }
}
