using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fabrica.Core.Threading;

namespace Fabrica.Core.Memory;

/// <summary>
/// Parallel refcount array with O(1) indexed access, single-threaded mutation, and iterative cascade-free.
/// Designed as a companion to <see cref="UnsafeSlabArena{T}"/>: each arena index maps to a refcount at the same
/// index in this table.
///
/// STORAGE
///   Uses <see cref="UnsafeSlabDirectory{T}"/> of <c>int</c> for the same two-level slab structure and O(1)
///   bit-shift indexing. Slab parameters for <c>int</c> (4 bytes) yield ~21,000 entries per slab under the LOH
///   threshold. Slabs are allocated upfront via <see cref="EnsureCapacity"/> rather than on each increment,
///   so a missing slab during <see cref="Increment"/> indicates a caller bug.
///
/// CASCADE FREE
///   <see cref="Decrement{TEvents,TChildren}"/> always cascades: when a refcount reaches zero, the table
///   iteratively processes freed nodes via a reusable worklist (<see cref="UnsafeStack{T}"/> field — no per-call
///   allocation). For each freed node, <see cref="IRefCountEvents.OnFreed"/> fires and
///   <see cref="IChildEnumerator.EnumerateChildren"/> discovers children to decrement.
///
///   Re-entrancy is supported for cross-table cascades (type A → type B → type A). When a cascade is already
///   in progress and a re-entrant <see cref="Decrement{TEvents,TChildren}"/> hits zero, the index is pushed
///   onto the existing worklist and the outer loop processes it — no nested loops, bounded stack depth.
///
/// STRUCT GENERIC PATTERN
///   <see cref="Decrement{TEvents,TChildren}"/> and <see cref="DecrementBatch{TEvents,TChildren}"/> take
///   struct type parameters constrained to <see cref="IRefCountEvents"/> and <see cref="IChildEnumerator"/>.
///   The JIT specializes per struct type, eliminating interface dispatch in hot paths.
///
/// THREAD MODEL
///   Single-threaded. All operations must come from one thread (the coordinator). Debug builds assert via
///   <see cref="SingleThreadedOwner"/>.
///
/// PORTABILITY
///   No GC reliance. Storage is <c>int[][]</c> via the directory. In Rust/C++ this maps to <c>Vec&lt;Box&lt;[i32]&gt;&gt;</c>.
///
/// PERFORMANCE (Apple M4 Max, .NET 10, Release)
///   Sequential increment: ~0.8 ns/op. Cascade-free (binary tree): ~2.4 ns/op. Steady-state inc/dec: ~2.5 ns/op.
///   See benchmarks/results/ for full tables.
/// </summary>
internal sealed class RefCountTable
{
    private const int DefaultDirectoryLength = 65_536;

    private readonly UnsafeSlabDirectory<int> _directory;
    private readonly UnsafeStack<int> _worklist = new();
    private bool _cascadeInProgress;
    private SingleThreadedOwner _owner;

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

    // ── Capacity management ──────────────────────────────────────────────

    /// <summary>
    /// Ensures all slab regions up to <paramref name="highWater"/> are allocated. The coordinator calls this
    /// after merging new arena allocations, before performing any increment/decrement operations on the new
    /// index range. Idempotent for already-allocated regions.
    /// </summary>
    public void EnsureCapacity(int highWater)
    {
        _owner.AssertOwnerThread();
        for (var slabStart = 0; slabStart < highWater; slabStart += _directory.SlabLength)
            _directory.EnsureSlab(slabStart);
    }

    // ── Core operations ──────────────────────────────────────────────────

    /// <summary>Returns the current refcount for the given index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetCount(int index)
        => _directory[index];

    /// <summary>Increments the refcount at the given index. The caller must have called <see cref="EnsureCapacity"/>
    /// for this index range beforehand.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Increment(int index)
    {
        _owner.AssertOwnerThread();
        _directory[index]++;
    }

    /// <summary>
    /// Decrements the refcount at the given index. If it reaches zero, iteratively cascades through children
    /// via the reusable worklist: <see cref="IRefCountEvents.OnFreed"/> fires for each freed node, and
    /// <see cref="IChildEnumerator.EnumerateChildren"/> discovers child indices to decrement.
    ///
    /// Re-entrant calls (from cross-table cascades) push onto the existing worklist instead of starting a
    /// nested loop, keeping call-stack depth bounded by the number of distinct tables rather than tree depth.
    /// </summary>
    public void Decrement<TEvents, TChildren>(int index, TEvents events, TChildren children)
        where TEvents : struct, IRefCountEvents
        where TChildren : struct, IChildEnumerator
    {
        _owner.AssertOwnerThread();
        Debug.Assert(_directory[index] > 0, $"Decrement on index {index} with refcount already at {_directory[index]}.");

        if (--_directory[index] != 0)
            return;

        if (_cascadeInProgress)
        {
            _worklist.Push(index);
            return;
        }

        _cascadeInProgress = true;
        _worklist.Push(index);

        while (_worklist.TryPop(out var current))
        {
            events.OnFreed(current);
            children.EnumerateChildren(current, this);
        }

        _cascadeInProgress = false;
    }

    /// <summary>
    /// Called by <see cref="IChildEnumerator.EnumerateChildren"/> implementations to decrement a child's
    /// refcount during cascade processing. If the child hits zero, it is pushed onto the worklist for the
    /// cascade loop to process. Must only be called during an active cascade.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecrementChild(int childIndex)
    {
        Debug.Assert(_cascadeInProgress, "DecrementChild must only be called during cascade processing.");
        Debug.Assert(_directory[childIndex] > 0, $"DecrementChild on index {childIndex} with refcount already at {_directory[childIndex]}.");
        if (--_directory[childIndex] == 0)
            _worklist.Push(childIndex);
    }

    // ── Bulk operations ──────────────────────────────────────────────────

    /// <summary>Increments refcounts for all indices in the span. The caller must have called
    /// <see cref="EnsureCapacity"/> for the full range beforehand.</summary>
    public void IncrementBatch(ReadOnlySpan<int> indices)
    {
        _owner.AssertOwnerThread();
        for (var i = 0; i < indices.Length; i++)
            _directory[indices[i]]++;
    }

    /// <summary>
    /// Decrements refcounts for all indices in the span, then cascades any that hit zero. Must not be called
    /// during an active cascade (i.e., not re-entrant — batch operations are top-level coordinator calls).
    /// </summary>
    public void DecrementBatch<TEvents, TChildren>(ReadOnlySpan<int> indices, TEvents events, TChildren children)
        where TEvents : struct, IRefCountEvents
        where TChildren : struct, IChildEnumerator
    {
        _owner.AssertOwnerThread();
        Debug.Assert(!_cascadeInProgress, "DecrementBatch must not be called during an active cascade.");

        for (var i = 0; i < indices.Length; i++)
        {
            var index = indices[i];
            Debug.Assert(_directory[index] > 0, $"DecrementBatch on index {index} with refcount already at {_directory[index]}.");
            if (--_directory[index] == 0)
                _worklist.Push(index);
        }

        if (_worklist.Count == 0)
            return;

        _cascadeInProgress = true;

        while (_worklist.TryPop(out var current))
        {
            events.OnFreed(current);
            children.EnumerateChildren(current, this);
        }

        _cascadeInProgress = false;
    }

    // ── Callback interfaces ──────────────────────────────────────────────

    /// <summary>Receives notifications when a refcount reaches zero.</summary>
    public interface IRefCountEvents
    {
        void OnFreed(int index);
    }

    /// <summary>Enumerates child indices of a node during cascade-free. Implementations call
    /// <see cref="DecrementChild"/> for each child index.</summary>
    public interface IChildEnumerator
    {
        void EnumerateChildren(int index, RefCountTable table);
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
        public bool CascadeInProgress => table._cascadeInProgress;
    }
}
