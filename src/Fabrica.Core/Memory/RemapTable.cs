using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Fabrica.Core.Memory;

/// <summary>
/// Maps <c>(threadId, localIndex)</c> to a global arena index during merge. Each worker thread has
/// its own mapping sequence; local index <c>i</c> on thread <c>t</c> maps to global index
/// <c>Resolve(t, i)</c>.
///
/// THREAD MODEL
///   Single-threaded. Built by the coordinator (or a per-type merge worker) during Phase 1, read
///   during Phase 2a. No synchronization.
/// </summary>
public sealed class RemapTable
{
    // Outer array: fixed size (thread count). Each inner list grows with mappings and retains backing across Reset.
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
    internal int Resolve(int threadId, int localIndex) => _remap[threadId][localIndex];

    /// <summary>
    /// If <paramref name="handle"/> is a local (tagged) handle, rewrites it to the corresponding
    /// global handle using the stored mappings. Global and <see cref="Handle{T}.None"/> handles
    /// are returned unchanged.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Handle<T> Remap<T>(Handle<T> handle) where T : struct
    {
        var index = handle.Index;
        if (!TaggedHandle.IsLocal(index))
            return handle;

        var threadId = TaggedHandle.DecodeThreadId(index);
        var localIndex = TaggedHandle.DecodeLocalIndex(index);
        return new Handle<T>(this.Resolve(threadId, localIndex));
    }

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
