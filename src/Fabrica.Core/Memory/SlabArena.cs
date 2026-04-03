using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
///   Free-list reuse (via FastStack with unchecked access): ~7.6x bump at 100K. Steady-state interleaved
///   alloc/free: ~4.3ns/op at 100K entries.
/// </summary>
internal sealed class SlabArena<T> where T : struct
{
    private const int DefaultDirectoryLength = 65_536;

    private readonly T[][] _directory;
    private readonly int _slabLength;
    private readonly int _slabShift;
    private readonly int _slabMask;

    private int _highWater;
    private int _count;
    private readonly FastStack<int> _freeList = new();

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
                $"SlabArena<{typeof(T).Name}> mutating operation called from thread {current} " +
                $"but owner is thread {_ownerThreadId}. Mutating operations are single-threaded.");
    }
#endif

    // ── Constructors ──────────────────────────────────────────────────────

    /// <summary>Creates an arena with the default directory length and LOH-aware slab sizing.</summary>
    public SlabArena()
        : this(DefaultDirectoryLength, SlabSizeHelper<T>.SlabShift)
    {
    }

    /// <summary>Creates an arena with caller-specified directory length and slab shift. Intended for tests that need small
    /// parameters to exercise edge cases without allocating large amounts of memory.</summary>
    internal SlabArena(int directoryLength, int slabShift)
    {
        _directory = new T[directoryLength][];
        _slabShift = slabShift;
        _slabLength = 1 << slabShift;
        _slabMask = _slabLength - 1;
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a reference to the entry at the given index. The caller must ensure the index was returned by
    /// <see cref="Allocate"/> and has not been freed. No bounds checking is performed in release builds.
    /// </summary>
    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            Debug.Assert(index >= 0 && index < _highWater, $"Index {index} is out of range [0, {_highWater}).");
            return ref this.GetEntry(index);
        }
    }

    /// <summary>
    /// Allocates an entry and returns its index. Reuses a freed entry (LIFO) when available, otherwise bumps the
    /// high-water mark and allocates a new slab if needed. The returned entry contains <c>default(T)</c> if freshly
    /// allocated, or the previous occupant's data if reused from the free list — callers must initialise it.
    /// </summary>
    public int Allocate()
    {
#if DEBUG
        this.AssertOwnerThread();
#endif
        int index;
        if (_freeList.TryPop(out var freed))
        {
            index = freed;
        }
        else
        {
            index = _highWater++;
            this.EnsureSlab(index);
        }

        _count++;
        return index;
    }

    /// <summary>
    /// Returns an entry to the free list for future reuse. The caller is responsible for ensuring the entry is no longer
    /// referenced. Does not clear the entry data — callers (or the coordinator) must handle cleanup.
    /// </summary>
    public void Free(int index)
    {
#if DEBUG
        this.AssertOwnerThread();
        Debug.Assert(index >= 0 && index < _highWater, $"Free index {index} is out of range [0, {_highWater}).");
#endif
        _freeList.Push(index);
        _count--;
    }

    // ── Internals ─────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref T GetEntry(int index)
    {
        var slabIndex = index >> _slabShift;
        var offset = index & _slabMask;

        Debug.Assert((uint)slabIndex < (uint)_directory.Length, $"Slab index {slabIndex} out of directory bounds [0, {_directory.Length}).");
        ref var slab = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_directory), slabIndex);

        Debug.Assert(slab is not null, $"Slab at index {slabIndex} is null.");
        Debug.Assert((uint)offset < (uint)slab!.Length, $"Offset {offset} out of slab bounds [0, {slab.Length}).");
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(slab), offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureSlab(int index)
    {
        var slabIndex = index >> _slabShift;

        Debug.Assert((uint)slabIndex < (uint)_directory.Length, $"Slab index {slabIndex} out of directory bounds [0, {_directory.Length}).");
        ref var slab = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_directory), slabIndex);
        slab ??= new T[_slabLength];
    }

    // ── Test accessor ─────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor()
        => new(this);

    internal readonly struct TestAccessor(SlabArena<T> arena)
    {
        public T[][] Directory => arena._directory;
        public int DirectoryLength => arena._directory.Length;
        public int Count => arena._count;
        public int HighWater => arena._highWater;
        public int FreeCount => arena._freeList.Count;
        public int SlabLength => arena._slabLength;
        public int SlabShift => arena._slabShift;
        public int SlabMask => arena._slabMask;
    }
}
