using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fabrica.Core.Collections.Unsafe;
using Fabrica.Core.Threading;

namespace Fabrica.Core.Memory;

/// <summary>
/// Slab-backed arena for value types with O(1) indexed access and LIFO free-list recycling. Designed as a building block
/// for persistent (immutable, structurally-shared) data structures where nodes reference each other via integer indices
/// rather than pointers.
///
/// STORAGE
///   A pre-allocated directory of slab references. Each slab is a <typeparamref name="T"/>[] whose length is a power-of-2
///   computed by <see cref="SlabSizeHelper{T}"/> to stay below the LOH threshold (~85,000 bytes). Slabs are allocated on
///   demand; the directory never grows or is replaced.
///
///   Lookup is O(1): <c>directory[index &gt;&gt; slabShift][index &amp; slabMask]</c> — two array loads.
///
///   Default capacity ceiling: 65,536 slabs x (LOH-limited entries/slab). For 64-byte structs: ~67 million entries (~4GB).
///
/// ALLOCATION
///   <see cref="Allocate"/> pops from a LIFO free list when available, otherwise bumps the high-water index. LIFO order
///   favours recently-freed (cache-hot) entries. When the high-water index crosses a slab boundary, a new slab is
///   allocated on demand.
///
/// THREAD MODEL
///   Single-threaded. All mutating operations (<see cref="Allocate"/>, <see cref="Free"/>, write via <see cref="this[int]"/>)
///   must come from one thread (the coordinator in a fork-join design). Read access via <see cref="this[int]"/> is safe from
///   any thread once the entry has been written and published. Debug builds assert owner-thread identity.
///
/// PORTABILITY
///   No GC reliance. All storage is arrays of value types and an array of array references (the directory). In Rust/C++
///   this maps to <c>Vec&lt;Box&lt;[T]&gt;&gt;</c> or equivalent. No finalizers, no weak references.
///
/// PERFORMANCE (see benchmarks/results/ for full tables)
///   Bump allocation: ~1ns/entry for small structs. Indexed read: ~0.8ns sequential, ~1ns random (zero-allocation).
///   Free-list reuse (via UnsafeStack with unchecked access): ~7.6x bump at 100K. Steady-state interleaved
///   alloc/free: ~4.3ns/op at 100K entries.
/// </summary>
internal sealed class UnsafeSlabArena<T> where T : struct
{
    private const int DefaultDirectoryLength = 65_536;

    private readonly UnsafeSlabDirectory<T> _directory;
    private int _highWater;
    private int _count;
    private readonly UnsafeStack<Handle<T>> _freeList = new();

    private SingleThreadedOwner _owner;

    // ── Constructors ──────────────────────────────────────────────────────

    /// <summary>Creates an arena with the default directory length and LOH-aware slab sizing.</summary>
    public UnsafeSlabArena()
        : this(DefaultDirectoryLength, SlabSizeHelper<T>.SlabShift)
    {
    }

    /// <summary>Creates an arena with caller-specified directory length and slab shift. Intended for tests that need small
    /// parameters to exercise edge cases without allocating large amounts of memory.</summary>
    internal UnsafeSlabArena(int directoryLength, int slabShift)
        => _directory = new UnsafeSlabDirectory<T>(directoryLength, slabShift);

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Exclusive upper bound on indices reserved by bump allocation (<see cref="Allocate"/>, <see cref="AllocateBatch"/>):
    /// valid handles satisfy <c>0 &lt;= index &lt; HighWater</c> (free-list reuse may still reference indices below this
    /// mark). Single-threaded readers may observe this after coordinator-side merges for sizing and iteration ranges.
    /// </summary>
    public int HighWater
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _highWater;
    }

    /// <summary>
    /// Returns a reference to the entry at the given handle. The caller must ensure the handle was returned by
    /// <see cref="Allocate"/> and has not been freed. No bounds checking is performed in release builds.
    /// </summary>
    public ref T this[Handle<T> handle]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            Debug.Assert(handle.Index >= 0 && handle.Index < _highWater, $"Index {handle.Index} is out of range [0, {_highWater}).");
            return ref _directory[handle.Index];
        }
    }

    /// <summary>
    /// Allocates an entry and returns its handle. Reuses a freed entry (LIFO) when available, otherwise bumps the
    /// high-water mark and allocates a new slab if needed. The returned entry contains <c>default(T)</c> if freshly
    /// allocated, or the previous occupant's data if reused from the free list — callers must initialise it.
    /// </summary>
    public Handle<T> Allocate()
    {
        _owner.AssertOwnerThread();
        Handle<T> handle;
        if (_freeList.TryPop(out var freed))
        {
            handle = freed;
        }
        else
        {
            var index = _highWater++;
            _directory.EnsureSlab(index);
            handle = new Handle<T>(index);
        }

        _count++;
        return handle;
    }

    /// <summary>
    /// Allocates <paramref name="count"/> contiguous entries by bumping the high-water mark, bypassing the free list.
    /// Returns the starting global index. The caller must initialise all entries in the range
    /// <c>[startIndex, startIndex + count)</c>. Used by the coordinator merge to batch-allocate global slots for an
    /// entire <see cref="ThreadLocalBuffer{T}"/>.
    /// </summary>
    public int AllocateBatch(int count)
    {
        _owner.AssertOwnerThread();
        Debug.Assert(count >= 0, $"AllocateBatch called with negative count {count}.");

        if (count == 0)
            return _highWater;

        var startIndex = _highWater;
        _highWater += count;
        _count += count;

        for (var i = startIndex; i < _highWater; i += _directory.SlabLength)
            _directory.EnsureSlab(i);

        _directory.EnsureSlab(_highWater - 1);

        return startIndex;
    }

    /// <summary>
    /// Returns an entry to the free list for future reuse. The caller is responsible for ensuring the entry is no longer
    /// referenced. Does not clear the entry data — callers (or the coordinator) must handle cleanup.
    /// </summary>
    public void Free(Handle<T> handle)
    {
        _owner.AssertOwnerThread();
        Debug.Assert(handle.Index >= 0 && handle.Index < _highWater, $"Free index {handle.Index} is out of range [0, {_highWater}).");
        _freeList.Push(handle);
        _count--;
    }

    // ── Test accessor ─────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor()
        => new(this);

    internal readonly struct TestAccessor(UnsafeSlabArena<T> arena)
    {
        public T[][] Directory => arena._directory.RawArray;
        public int DirectoryLength => arena._directory.DirectoryLength;
        public int Count => arena._count;
        public int HighWater => arena.HighWater;
        public int FreeCount => arena._freeList.Count;
        public int SlabLength => arena._directory.SlabLength;
        public int SlabShift => arena._directory.SlabShift;
        public int SlabMask => arena._directory.SlabMask;
    }
}
