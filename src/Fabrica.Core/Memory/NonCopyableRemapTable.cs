using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fabrica.Core.Collections.Unsafe;

namespace Fabrica.Core.Memory;

/// <summary>
/// Maps <c>(threadId, localIndex)</c> to a global arena index during merge. Each worker thread has
/// its own mapping sequence; local index <c>i</c> on thread <c>t</c> maps to global index
/// <c>Resolve(t, i)</c>.
///
/// Storage is flattened into a single contiguous <see cref="NonCopyableUnsafeList{T}"/> with
/// per-thread base offsets for optimal cache locality during resolve operations.
///
/// THREAD MODEL
///   Single-threaded. Built by the coordinator (or a per-type merge worker) during Phase 1, read
///   during Phase 2a. No synchronization.
///
/// WARNING: This is a mutable struct backed by heap arrays. Do not copy by value — mutations
/// will not propagate to the copy. Always store in a single location and pass by reference.
/// </summary>
internal struct NonCopyableRemapTable
{
    /// <summary>
    /// Single backing list. The first <see cref="_threadCount"/> elements are per-thread base offsets;
    /// all subsequent elements are the actual mapping data. <c>Resolve(t, i)</c> reads
    /// <c>_data[_data[t] + i]</c> — two indexed reads into the same array.
    /// </summary>
    private NonCopyableUnsafeList<int> _data;
    private readonly int _threadCount;

    public NonCopyableRemapTable(int threadCount)
    {
        Debug.Assert(threadCount > 0, $"NonCopyableRemapTable requires at least 1 thread, got {threadCount}.");
        _threadCount = threadCount;
        _data = NonCopyableUnsafeList<int>.Create();
        for (var i = 0; i < threadCount; i++)
            _data.Add(0);
    }

    /// <summary>Number of threads this remap table supports.</summary>
    public readonly int ThreadCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _threadCount;
    }

    /// <summary>
    /// Records that local index <paramref name="localIndex"/> on thread <paramref name="threadId"/>
    /// maps to <paramref name="globalIndex"/>. Mappings must be added in order (local index 0, 1, 2, ...)
    /// for each thread, and threads must be processed in ascending order.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetMapping(int threadId, int localIndex, int globalIndex)
    {
        if (localIndex == 0)
            _data[threadId] = _data.Count;
        Debug.Assert(localIndex == _data.Count - _data[threadId],
            $"SetMapping out of order: expected localIndex {_data.Count - _data[threadId]}, got {localIndex}.");
        _data.Add(globalIndex);
    }

    /// <summary>Returns a read-only view of this remap table, suitable for the resolve/remap phase.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ReadOnlyRemapTable AsReadOnly() => new(_data, _threadCount);

    /// <summary>
    /// Resets all mappings to empty. The backing array is retained so the next merge pass incurs
    /// no allocation in steady state.
    /// </summary>
    public void Reset()
    {
        _data.WrittenSpanMutable[.._threadCount].Clear();
        _data.Count = _threadCount;
    }
}
