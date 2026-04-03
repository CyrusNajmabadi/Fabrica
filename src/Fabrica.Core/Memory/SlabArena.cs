using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Fabrica.Core.Memory;

/// <summary>
/// Slab-backed arena for value types with O(1) indexed access and LIFO free-list recycling. Designed as a building block
/// for persistent (immutable, structurally-shared) data structures where nodes reference each other via integer indices
/// rather than pointers.
///
/// STORAGE
///   A pre-allocated directory of <see cref="DirectoryLength"/> slab references (65,536 entries, ~512KB). Each slab is a
///   <typeparamref name="T"/>[] whose length is a power-of-2 computed by <see cref="SlabSizeHelper"/> to stay below the
///   LOH threshold (~85,000 bytes). Slabs are allocated on demand; the directory never grows or is replaced.
///
///   Lookup is O(1): <c>directory[index &gt;&gt; slabShift][index &amp; slabMask]</c> — two array loads.
///
///   Capacity ceiling: 65,536 slabs x (LOH-limited nodes/slab). For 64-byte structs: ~67 million nodes (~4GB).
///
/// ALLOCATION
///   <see cref="Allocate"/> pops from a LIFO free list when available, otherwise bumps the high-water index. LIFO order
///   favours recently-freed (cache-hot) slots. When the high-water index crosses a slab boundary, a new slab is allocated
///   on demand.
///
/// THREAD MODEL
///   Single-threaded. All mutating operations (<see cref="Allocate"/>, <see cref="Free"/>, write via <see cref="this[int]"/>)
///   must come from one thread (the coordinator in a fork-join design). Read access via <see cref="this[int]"/> is safe from
///   any thread once the slot has been written and published. Debug builds assert owner-thread identity.
///
/// PORTABILITY
///   No GC reliance. All storage is arrays of value types and an array of array references (the directory). In Rust/C++
///   this maps to <c>Vec&lt;Box&lt;[T]&gt;&gt;</c> or equivalent. No finalizers, no weak references.
/// </summary>
public sealed class SlabArena<T> where T : struct
{
    /// <summary>Number of slab references in the pre-allocated directory. 65,536 entries ≈ 512KB of pointers.</summary>
    internal const int DirectoryLength = 65_536;

    private readonly T[][] _directory = new T[DirectoryLength][];

    /// <summary>Total number of slots that have ever been bump-allocated (not counting free-list reuse).</summary>
    private int _highWater;

    /// <summary>Number of currently allocated (live) slots. Incremented by <see cref="Allocate"/>, decremented by
    /// <see cref="Free"/>.</summary>
    private int _count;

    // ── Free list (LIFO stack stored as a simple list of freed indices) ────

    private int[] _freeList = new int[1024];
    private int _freeCount;

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

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>Number of currently allocated (live) slots.</summary>
    public int Count => _count;

    /// <summary>Total number of slots that have ever been bump-allocated. Includes freed slots.</summary>
    public int HighWater => _highWater;

    /// <summary>Number of freed slots available for reuse.</summary>
    public int FreeCount => _freeCount;

    /// <summary>
    /// Returns a reference to the slot at the given index. The caller must ensure the index is valid (was returned by
    /// <see cref="Allocate"/> and has not been freed). No bounds checking is performed in release builds.
    /// </summary>
    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            Debug.Assert(index >= 0 && index < _highWater, $"Index {index} is out of range [0, {_highWater}).");
            return ref _directory[index >> SlabSizeHelper.SlabShift][index & SlabSizeHelper.OffsetMask];
        }
    }

    /// <summary>
    /// Allocates a slot and returns its index. Reuses a freed slot (LIFO) when available, otherwise bumps the high-water
    /// mark and allocates a new slab if needed. The returned slot contains <c>default(T)</c> if freshly allocated, or the
    /// previous occupant's data if reused from the free list — callers must initialise it.
    /// </summary>
    public int Allocate()
    {
#if DEBUG
        this.AssertOwnerThread();
#endif
        int index;
        if (_freeCount > 0)
        {
            index = _freeList[--_freeCount];
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
    /// Returns a slot to the free list for future reuse. The caller is responsible for ensuring the slot is no longer
    /// referenced. Does not clear the slot data — callers (or the coordinator) must handle cleanup.
    /// </summary>
    public void Free(int index)
    {
#if DEBUG
        this.AssertOwnerThread();
        Debug.Assert(index >= 0 && index < _highWater, $"Free index {index} is out of range [0, {_highWater}).");
#endif
        if (_freeCount == _freeList.Length)
            Array.Resize(ref _freeList, _freeList.Length * 2);

        _freeList[_freeCount++] = index;
        _count--;
    }

    // ── Internals ─────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureSlab(int index)
    {
        var slabIndex = index >> SlabSizeHelper.SlabShift;
        if (_directory[slabIndex] is null)
            _directory[slabIndex] = new T[SlabSizeHelper.SlabLength];
    }

    // ── Slab sizing (same pattern as ProducerConsumerQueue.SlabSizeHelper) ─

    internal static class SlabSizeHelper
    {
        private const int LargeObjectHeapThreshold = 85_000;
        private const int ArrayBaseOverhead = 32;

        /// <summary>Number of items per slab — always a power of 2.</summary>
        public static readonly int SlabLength;

        /// <summary><c>log2(SlabLength)</c> — for bit-shift indexing.</summary>
        public static readonly int SlabShift;

        /// <summary><c>SlabLength - 1</c> — for bitwise-AND offset masking.</summary>
        public static readonly int OffsetMask;

        static SlabSizeHelper()
        {
            var itemSize = Unsafe.SizeOf<T>();
            var maxElements = Math.Max((LargeObjectHeapThreshold - ArrayBaseOverhead) / itemSize, 1);

            SlabLength = (int)BitOperations.RoundUpToPowerOf2((uint)(maxElements + 1)) >> 1;
            if (SlabLength == 0)
                SlabLength = 1;

            SlabShift = BitOperations.Log2((uint)SlabLength);
            OffsetMask = SlabLength - 1;
        }
    }

    // ── Test accessor ─────────────────────────────────────────────────────

    internal TestAccessor GetTestAccessor()
        => new(this);

    internal readonly struct TestAccessor(SlabArena<T> arena)
    {
        public T[][] Directory => arena._directory;
        public int HighWater => arena._highWater;
        public int FreeCount => arena._freeCount;
        public ReadOnlySpan<int> FreeList => arena._freeList.AsSpan(0, arena._freeCount);

        public int SlabLength => SlabSizeHelper.SlabLength;
        public int SlabShift => SlabSizeHelper.SlabShift;
        public int OffsetMask => SlabSizeHelper.OffsetMask;
    }
}
