using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Fabrica.Core.Memory;

/// <summary>
/// Parallel refcount array with O(1) indexed access, single-threaded mutation, and iterative cascade-free.
/// Designed as a companion to <see cref="UnsafeSlabArena{T}"/>: each arena index maps to a refcount at the same
/// index in this table.
///
/// STORAGE
///   Uses <see cref="UnsafeSlabDirectory{T}"/> of <c>int</c> for the same two-level slab structure and O(1)
///   bit-shift indexing. Slab parameters for <c>int</c> (4 bytes) yield ~21,000 entries per slab under the LOH
///   threshold. Slabs are allocated on demand when a refcount is first set for an index in a new region.
///
/// CASCADE FREE
///   When <see cref="Decrement"/> or <see cref="DecrementCascade"/> drives a refcount to zero, the table notifies
///   the caller via an <see cref="IRefCountEvents"/> callback. <see cref="DecrementCascade"/> uses an iterative
///   worklist (not recursion) to propagate zero-refcount frees through child references, bounding stack depth
///   regardless of tree shape.
///
/// THREAD MODEL
///   Single-threaded. All operations must come from one thread (the coordinator). Debug builds assert owner-thread
///   identity.
///
/// PORTABILITY
///   No GC reliance. Storage is <c>int[][]</c> via the directory. In Rust/C++ this maps to <c>Vec&lt;Box&lt;[i32]&gt;&gt;</c>.
///
/// PERFORMANCE (Apple M4 Max, .NET 10, Release)
///   Sequential increment: ~1.2 ns/op. Cascade-free (binary tree): ~2.8 ns/op. Steady-state inc/dec: ~3.6 ns/op.
///   See benchmarks/results/ for full tables.
/// </summary>
internal sealed class RefCountTable
{
    private const int DefaultDirectoryLength = 65_536;

    private readonly UnsafeSlabDirectory<int> _directory;

    // ── Debug thread-ownership tracking ───────────────────────────────────

#if DEBUG
    private int _ownerThreadId = -1;

    private void AssertOwnerThread()
    {
        var current = Environment.CurrentManagedThreadId;
        if (_ownerThreadId == -1)
            _ownerThreadId = current;
        else
            Debug.Assert(
                _ownerThreadId == current,
                $"RefCountTable mutating operation called from thread {current} " +
                $"but owner is thread {_ownerThreadId}. Mutating operations are single-threaded.");
    }
#endif

    // ── Constructors ──────────────────────────────────────────────────────

    /// <summary>Creates a table with the default directory length and LOH-aware slab sizing for <c>int</c>.</summary>
    public RefCountTable()
        : this(DefaultDirectoryLength, SlabSizeHelper<int>.SlabShift)
    {
    }

    /// <summary>Creates a table with caller-specified directory length and slab shift. Intended for tests that need
    /// small parameters to exercise edge cases without allocating large amounts of memory.</summary>
    internal RefCountTable(int directoryLength, int slabShift)
        => _directory = new UnsafeSlabDirectory<int>(directoryLength, slabShift);

    // ── Core operations ──────────────────────────────────────────────────

    /// <summary>Returns the current refcount for the given index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetCount(int index)
        => _directory[index];

    /// <summary>Increments the refcount at the given index. Allocates the backing slab on demand if needed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Increment(int index)
    {
#if DEBUG
        this.AssertOwnerThread();
#endif
        _directory.EnsureSlab(index);
        _directory[index]++;
    }

    /// <summary>
    /// Decrements the refcount at the given index. If the refcount reaches zero, invokes
    /// <see cref="IRefCountEvents.OnFreed"/> on the callback.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Decrement(int index, IRefCountEvents events)
    {
#if DEBUG
        this.AssertOwnerThread();
        Debug.Assert(_directory[index] > 0, $"Decrement on index {index} with refcount already at {_directory[index]}.");
#endif
        if (--_directory[index] == 0)
            events.OnFreed(index);
    }

    /// <summary>
    /// Decrements the refcount at the given index and, if it reaches zero, iteratively cascades through children
    /// using a worklist. For each freed node, <see cref="IRefCountEvents.OnFreed"/> is called and
    /// <see cref="IChildEnumerator.EnumerateChildren"/> is used to discover child indices to decrement.
    ///
    /// The worklist is iterative — no recursion — so arbitrarily deep trees cannot overflow the stack.
    /// </summary>
    public void DecrementCascade(int index, IRefCountEvents events, IChildEnumerator children)
    {
#if DEBUG
        this.AssertOwnerThread();
        Debug.Assert(_directory[index] > 0, $"DecrementCascade on index {index} with refcount already at {_directory[index]}.");
#endif
        if (--_directory[index] != 0)
            return;

        var worklist = new UnsafeStack<int>();
        worklist.Push(index);

        while (worklist.TryPop(out var current))
        {
            events.OnFreed(current);
            children.EnumerateChildren(current, ref worklist, this);
        }
    }

    /// <summary>
    /// Called by <see cref="IChildEnumerator.EnumerateChildren"/> implementations to decrement a child's refcount.
    /// If the child's refcount reaches zero, it is pushed onto the worklist for further cascade processing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecrementChild(int childIndex, ref UnsafeStack<int> worklist)
    {
        Debug.Assert(_directory[childIndex] > 0, $"DecrementChild on index {childIndex} with refcount already at {_directory[childIndex]}.");
        if (--_directory[childIndex] == 0)
            worklist.Push(childIndex);
    }

    // ── Bulk operations ──────────────────────────────────────────────────

    /// <summary>Increments refcounts for all indices in the span.</summary>
    public void IncrementBatch(ReadOnlySpan<int> indices)
    {
#if DEBUG
        this.AssertOwnerThread();
#endif
        for (var i = 0; i < indices.Length; i++)
        {
            var index = indices[i];
            _directory.EnsureSlab(index);
            _directory[index]++;
        }
    }

    /// <summary>Decrements refcounts for all indices in the span, invoking the callback for any that reach zero.</summary>
    public void DecrementBatch(ReadOnlySpan<int> indices, IRefCountEvents events)
    {
#if DEBUG
        this.AssertOwnerThread();
#endif
        for (var i = 0; i < indices.Length; i++)
        {
            var index = indices[i];
            Debug.Assert(_directory[index] > 0, $"DecrementBatch on index {index} with refcount already at {_directory[index]}.");
            if (--_directory[index] == 0)
                events.OnFreed(index);
        }
    }

    // ── Callback interfaces ──────────────────────────────────────────────

    /// <summary>Receives notifications when a refcount reaches zero.</summary>
    public interface IRefCountEvents
    {
        void OnFreed(int index);
    }

    /// <summary>Enumerates child indices of a node during cascade-free. Implementations call
    /// <see cref="DecrementChild"/> for each child.</summary>
    public interface IChildEnumerator
    {
        void EnumerateChildren(int index, ref UnsafeStack<int> worklist, RefCountTable table);
    }

    // ── Test accessor ─────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor()
        => new(this);

    internal readonly struct TestAccessor(RefCountTable table)
    {
        public int[][] Directory => table._directory.RawArray;
        public int DirectoryLength => table._directory.DirectoryLength;
        public int SlabLength => table._directory.SlabLength;
        public int SlabShift => table._directory.SlabShift;
    }
}
