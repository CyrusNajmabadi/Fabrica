using System.Runtime.CompilerServices;
using Fabrica.Core.Collections.Unsafe;

namespace Fabrica.Core.Memory;

/// <summary>
/// Append-only buffer for nodes created by a single worker thread during a parallel work phase.
/// Each worker gets one <see cref="ThreadLocalBuffer{T}"/> per node type.
///
/// ALLOCATION
///   <see cref="Allocate"/> appends a default <typeparamref name="T"/> and returns a local
///   <see cref="Handle{T}"/> with the thread ID baked in via <see cref="TaggedHandle.EncodeLocal"/>.
///   The caller writes the node data through the returned handle's index into <see cref="this[int]"/>.
///
/// REUSE
///   <see cref="Reset"/> sets the count to zero without releasing the backing array, so
///   steady-state usage incurs no allocation after warmup.
///
/// THREAD MODEL
///   Single-threaded during the work phase (the owning worker). Single-threaded during
///   the merge phase (the coordinator or a merge-worker for this type). No synchronization.
/// </summary>
public readonly struct ThreadLocalBuffer<T>(int threadId, int initialCapacity = 1024) where T : struct
{
    private readonly UnsafeList<T> _list = new(initialCapacity);
    private readonly UnsafeList<Handle<T>> _roots = new(initialCapacity: 64);
    private readonly int _threadId = threadId;

    /// <summary>Number of nodes allocated in this buffer during the current work phase.</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _list.Count;
    }

    /// <summary>
    /// Allocates a slot for a new node and returns a local <see cref="Handle{T}"/> encoding this
    /// thread's ID and the local index. The caller must write the node data via <see cref="this[int]"/>
    /// using the decoded local index.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Handle<T> Allocate() => this.Allocate(isRoot: false);

    /// <summary>
    /// Allocates a slot for a new node and returns a local <see cref="Handle{T}"/>. If
    /// <paramref name="isRoot"/> is true, the returned handle is also recorded as a root.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Handle<T> Allocate(bool isRoot)
    {
        var localIndex = _list.Count;
        _list.Add(default);
        var handle = new Handle<T>(TaggedHandle.EncodeLocal(_threadId, localIndex));
        if (isRoot)
            _roots.Add(handle);
        return handle;
    }

    /// <summary>
    /// Records an existing handle as a root. The handle may reference a node in any TLB, not just
    /// this one — the coordinator will remap it during the merge.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkRoot(Handle<T> handle) => _roots.Add(handle);

    /// <summary>All root handles recorded during the current work phase.</summary>
    public ReadOnlySpan<Handle<T>> RootHandles
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _roots.WrittenSpan;
    }

    /// <summary>
    /// Returns a reference to the node for the given handle (which must be a local handle
    /// belonging to this buffer). Decodes the local index internally.
    /// </summary>
    public ref T this[Handle<T> handle]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _list[TaggedHandle.DecodeLocalIndex(handle.Index)];
    }

    private ref T this[int localIndex]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _list[localIndex];
    }

    /// <summary>All nodes written during the current work phase. Read by the coordinator after the join barrier.</summary>
    public ReadOnlySpan<T> WrittenSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _list.WrittenSpan;
    }

    /// <summary>
    /// Resets the count to zero for the next work phase. Both the node list and root list are
    /// cleared; backing arrays are retained for steady-state zero allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        _list.Reset();
        _roots.Reset();
    }
}
