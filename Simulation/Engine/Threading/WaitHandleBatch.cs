namespace Simulation.Engine;

/// <summary>
/// Waits on an arbitrary number of <see cref="WaitHandle"/> instances by
/// chunking them into groups of at most 64 (the OS/runtime limit for
/// <see cref="WaitHandle.WaitAll"/>).
///
/// Chunk arrays are allocated once at construction time, so repeated
/// <see cref="WaitAll"/> calls on the hot path never allocate.
///
/// With <see cref="AutoResetEvent"/> handles, each <see cref="WaitHandle.WaitAll"/>
/// call atomically resets all handles in that chunk when they are all signaled,
/// so no manual reset is needed between ticks.
/// </summary>
internal sealed class WaitHandleBatch
{
    private const int MaxHandlesPerChunk = 64;

    private readonly WaitHandle[][] _chunks;

    public WaitHandleBatch(WaitHandle[] handles)
    {
        var chunkCount = (handles.Length + MaxHandlesPerChunk - 1) / MaxHandlesPerChunk;
        _chunks = new WaitHandle[chunkCount][];

        for (var i = 0; i < chunkCount; i++)
        {
            var start = i * MaxHandlesPerChunk;
            var length = Math.Min(MaxHandlesPerChunk, handles.Length - start);
            _chunks[i] = new WaitHandle[length];
            Array.Copy(handles, start, _chunks[i], 0, length);
        }
    }

    /// <summary>
    /// Blocks until every handle in every chunk is signaled.
    /// Chunks are waited on sequentially — all handles within a chunk
    /// must be signaled before the next chunk is checked.  Because we
    /// need ALL handles to complete regardless, the sequential ordering
    /// between chunks has no correctness or practical performance impact.
    /// </summary>
    public void WaitAll()
    {
        foreach (var chunk in _chunks)
            WaitHandle.WaitAll(chunk);
    }
}
