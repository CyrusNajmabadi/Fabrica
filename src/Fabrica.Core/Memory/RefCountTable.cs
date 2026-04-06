using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fabrica.Core.Threading;

namespace Fabrica.Core.Memory;

/// <summary>
/// Parallel refcount array with O(1) indexed access and single-threaded mutation.
/// Generic on <typeparamref name="T"/> to prevent accidental cross-type index misuse: a
/// <c>RefCountTable&lt;MeshNode&gt;</c> only accepts <see cref="Handle{T}"/> of <c>MeshNode</c>.
///
/// STORAGE
///   Uses <see cref="UnsafeSlabDirectory{T}"/> of <c>int</c> for the same two-level slab structure and O(1)
///   bit-shift indexing. Slab parameters for <c>int</c> (4 bytes) yield ~21,000 entries per slab under the LOH
///   threshold. Slabs are allocated upfront via <see cref="EnsureCapacity"/> rather than on each increment,
///   so a missing slab during <see cref="Increment"/> indicates a caller bug.
///
/// CASCADE-FREE OWNERSHIP
///   This table is a pure refcount array — it does not own cascade logic. When a refcount reaches
///   zero, <see cref="Decrement"/> returns <c>true</c> and the caller (typically
///   <see cref="GlobalNodeStore{TNode,TNodeOps}"/>) is responsible for cascading child decrements and
///   freeing the arena slot.
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
internal sealed class RefCountTable<T> where T : struct
{
    private const int DefaultDirectoryLength = 65_536;

    private readonly UnsafeSlabDirectory<int> _directory;

#if DEBUG
    private SingleThreadedOwner _owner;
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

    // ── Thread ownership ──────────────────────────────────────────────────

    /// <summary>Debug-only assertion that the caller is on the owner thread. Types that wrap a
    /// <see cref="RefCountTable{T}"/> (e.g., <see cref="GlobalNodeStore{TNode, TNodeOps}"/>) delegate
    /// here rather than maintaining their own <see cref="SingleThreadedOwner"/>.</summary>
    [Conditional("DEBUG")]
    internal void AssertOwnerThread()
    {
#if DEBUG
        _owner.AssertOwnerThread();
#endif
    }

    // ── Capacity management ──────────────────────────────────────────────

    /// <summary>
    /// Ensures all slab regions up to <paramref name="highWater"/> are allocated. The coordinator calls this
    /// after merging new arena allocations, before performing any increment/decrement operations on the new
    /// index range. Idempotent for already-allocated regions.
    /// </summary>
    public void EnsureCapacity(int highWater)
    {
        this.AssertOwnerThread();
        for (var slabStart = 0; slabStart < highWater; slabStart += _directory.SlabLength)
            _directory.EnsureSlab(slabStart);
    }

    // ── Core operations ──────────────────────────────────────────────────

    /// <summary>Returns the current refcount for the given handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetCount(Handle<T> handle)
        => _directory[handle.Index];

    /// <summary>Increments the refcount at the given handle. The caller must have called <see cref="EnsureCapacity"/>
    /// for this index range beforehand.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Increment(Handle<T> handle)
    {
        this.AssertOwnerThread();
        _directory[handle.Index]++;
    }

    /// <summary>
    /// Decrements the refcount at the given handle. Returns <c>true</c> if the refcount reached zero,
    /// indicating that the caller should cascade (enumerate children, decrement their refcounts, and
    /// free the arena slot).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Decrement(Handle<T> handle)
    {
        this.AssertOwnerThread();
        Debug.Assert(_directory[handle.Index] > 0, $"Decrement on index {handle.Index} with refcount already at {_directory[handle.Index]}.");

        return --_directory[handle.Index] == 0;
    }

    // ── Bulk operations ──────────────────────────────────────────────────

    /// <summary>Increments refcounts for all handles in the span. The caller must have called
    /// <see cref="EnsureCapacity"/> for the full range beforehand.</summary>
    public void IncrementBatch(ReadOnlySpan<Handle<T>> handles)
    {
        this.AssertOwnerThread();
        for (var i = 0; i < handles.Length; i++)
            _directory[handles[i].Index]++;
    }

    /// <summary>
    /// Decrements refcounts for all handles in the span. Handles whose refcount reaches zero are pushed
    /// onto <paramref name="hitZero"/> for the caller to cascade. Must not be called during an active
    /// cascade (i.e., only from top-level coordinator operations).
    /// </summary>
    public void DecrementBatch(ReadOnlySpan<Handle<T>> handles, UnsafeStack<Handle<T>> hitZero)
    {
        this.AssertOwnerThread();

        for (var i = 0; i < handles.Length; i++)
        {
            var index = handles[i].Index;
            Debug.Assert(_directory[index] > 0, $"DecrementBatch on index {index} with refcount already at {_directory[index]}.");
            if (--_directory[index] == 0)
                hitZero.Push(handles[i]);
        }
    }

    // ── Test accessor ─────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor()
        => new(this);

    internal readonly struct TestAccessor(RefCountTable<T> table)
    {
        public int[][] Directory => table._directory.RawArray;
        public int DirectoryLength => table._directory.DirectoryLength;
        public int SlabLength => table._directory.SlabLength;
        public int SlabShift => table._directory.SlabShift;
    }
}
