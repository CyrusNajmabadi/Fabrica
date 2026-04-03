using System.Diagnostics;
using System.Runtime.CompilerServices;
#if !DEBUG
using System.Runtime.InteropServices;
#endif
using Fabrica.Core.Threading;

namespace Fabrica.Core.Memory;

/// <summary>
/// Append-only per-thread buffer for new arena nodes. A worker allocates one before the work phase, appends new
/// nodes to it, and returns it to the coordinator at the join point. The coordinator then merges all buffers into
/// the global <see cref="UnsafeSlabArena{T}"/> and <see cref="RefCountTable"/>.
///
/// ALLOCATION
///   <see cref="Append"/> returns a raw local index (0, 1, 2, …). Workers tag this with
///   <see cref="ArenaIndex.TagLocal"/> when storing it as a child reference in another new node.
///
/// RELEASE LOG
///   Workers record global indices they want to release (e.g., old roots the consumer is done with) via
///   <see cref="LogRelease"/>. The coordinator collects these and decrements their refcounts during merge.
///   Uses <see cref="UnsafeStack{T}"/> — the same LIFO stack type used by the arena and refcount table.
///
/// REUSE
///   <see cref="Clear"/> resets node count and release log without freeing backing storage, so a buffer can be
///   handed back to a worker for the next frame with zero allocation in steady state.
///
/// THREAD MODEL
///   Single-threaded per buffer. One worker writes during the work phase; the coordinator reads during merge.
///   Debug builds assert via <see cref="SingleThreadedOwner"/>.
///
/// PORTABILITY
///   No GC reliance. In Rust: <c>Vec&lt;T&gt;</c> + <c>Vec&lt;i32&gt;</c>. In C++: <c>std::vector</c>.
/// </summary>
internal sealed class ThreadLocalBuffer<T> where T : struct
{
    private const int DefaultInitialCapacity = 256;

    private T[] _nodes;
    private int _count;
    private readonly UnsafeStack<int> _releaseLog = new();

    private SingleThreadedOwner _owner;

    public ThreadLocalBuffer() : this(DefaultInitialCapacity) { }

    internal ThreadLocalBuffer(int initialCapacity)
        => _nodes = new T[initialCapacity];

    /// <summary>Number of nodes appended since last <see cref="Clear"/>.</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    /// <summary>
    /// Appends a new node and returns its local (untagged) index. The caller should use
    /// <see cref="ArenaIndex.TagLocal"/> when storing this index as a child reference in another node.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Append(T node)
    {
        _owner.AssertOwnerThread();
        var index = _count;

        if (index == _nodes.Length)
            this.Grow();

#if DEBUG
        _nodes[index] = node;
#else
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_nodes), index) = node;
#endif
        _count = index + 1;
        return index;
    }

    /// <summary>Returns a reference to the node at the given local index.</summary>
    public ref T this[int localIndex]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            Debug.Assert(localIndex >= 0 && localIndex < _count, $"Local index {localIndex} is out of range [0, {_count}).");
#if DEBUG
            return ref _nodes[localIndex];
#else
            return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_nodes), localIndex);
#endif
        }
    }

    /// <summary>Records a global index that should be released (refcount decremented) during the coordinator's merge
    /// pass. Workers use this to signal "I'm done with this root".</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LogRelease(int globalIndex)
    {
        _owner.AssertOwnerThread();
        Debug.Assert(ArenaIndex.IsGlobal(globalIndex), $"LogRelease expects a global index (>= 0), got {globalIndex}.");
        _releaseLog.Push(globalIndex);
    }

    /// <summary>Returns a read-only span over the appended nodes. Valid until the next <see cref="Append"/> or
    /// <see cref="Clear"/>.</summary>
    public ReadOnlySpan<T> Nodes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nodes.AsSpan(0, _count);
    }

    /// <summary>Number of entries in the release log. The coordinator uses this to pre-size its release batch
    /// array before draining.</summary>
    public int ReleaseCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _releaseLog.Count;
    }

    /// <summary>Pops one entry from the release log. Returns <c>false</c> when the log is empty. The coordinator
    /// calls this in a loop to collect all releases without allocating a delegate or intermediate list.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPopRelease(out int globalIndex)
        => _releaseLog.TryPop(out globalIndex);

    /// <summary>Resets the buffer for reuse. Node count goes to zero and the release log is drained. Backing
    /// arrays are retained so the next work phase allocates nothing in steady state.</summary>
    public void Clear()
    {
        _count = 0;
        while (_releaseLog.TryPop(out _)) { }
        _owner = default;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow()
    {
        var newArray = new T[_nodes.Length * 2];
        Array.Copy(_nodes, newArray, _nodes.Length);
        _nodes = newArray;
    }

    // ── Test accessor ─────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor()
        => new(this);

    internal readonly struct TestAccessor(ThreadLocalBuffer<T> buffer)
    {
        public int Capacity => buffer._nodes.Length;
        public int ReleaseLogCount => buffer._releaseLog.Count;
    }
}
