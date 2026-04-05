using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fabrica.Core.Threading;

namespace Fabrica.Core.Memory;

/// <summary>
/// Parallel refcount array with O(1) indexed access, single-threaded mutation, and iterative cascade-free.
/// Generic on <typeparamref name="T"/> to prevent accidental cross-type index misuse: a
/// <c>RefCountTable&lt;MeshNode&gt;</c> only accepts <see cref="Handle{T}"/> of <c>MeshNode</c>.
///
/// STORAGE
///   Uses <see cref="UnsafeSlabDirectory{T}"/> of <c>int</c> for the same two-level slab structure and O(1)
///   bit-shift indexing. Slab parameters for <c>int</c> (4 bytes) yield ~21,000 entries per slab under the LOH
///   threshold. Slabs are allocated upfront via <see cref="EnsureCapacity"/> rather than on each increment,
///   so a missing slab during <see cref="Increment"/> indicates a caller bug.
///
/// CASCADE FREE
///   <see cref="Decrement{THandler}"/> always cascades: when a refcount reaches zero, the table iteratively
///   processes freed nodes via a reusable stack (<see cref="_cascadePending"/>). For each freed node,
///   <see cref="IRefCountHandler.OnFreed"/> fires. The handler is responsible for both freeing the node from
///   the arena and decrementing child refcounts via <see cref="Decrement{THandler}"/> — which, during an
///   active cascade, simply pushes zero-refcount children onto the pending stack for the outer loop.
///
///   Re-entrancy is supported for cross-table cascades (type A → type B → type A). When a cascade is already
///   active and a re-entrant <see cref="Decrement{THandler}"/> hits zero, the index is pushed onto the pending
///   stack and the outer loop processes it — no nested loops, bounded stack depth.
///
/// STRUCT GENERIC PATTERN
///   <see cref="Decrement{THandler}"/> and <see cref="DecrementBatch{THandler}"/> take a struct type parameter
///   constrained to <see cref="IRefCountHandler"/>. The JIT specializes per struct type, eliminating interface
///   dispatch in hot paths.
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
internal sealed partial class RefCountTable<T> where T : struct
{
    private const int DefaultDirectoryLength = 65_536;

    private readonly UnsafeSlabDirectory<int> _directory;

    /// <summary>
    /// Handles whose refcount reached zero during a decrement cascade, pending processing (the handler's
    /// <see cref="IRefCountHandler.OnFreed"/> callback). Reused across cascade operations to avoid allocation.
    /// </summary>
    private readonly UnsafeStack<Handle<T>> _cascadePending = new();

    /// <summary>
    /// True while a decrement cascade is being processed. When a <see cref="Decrement{THandler}"/> call hits
    /// zero during an active cascade (e.g., a child decrement within <see cref="IRefCountHandler.OnFreed"/>,
    /// or a cross-table A→B→A bounce), the handle is pushed onto <see cref="_cascadePending"/> and the caller
    /// returns immediately — the outer cascade loop will process it.
    /// </summary>
    private bool _cascadeActive;

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
    /// <see cref="RefCountTable{T}"/> (e.g., <see cref="NodeStore{TNode, THandler}"/>) delegate
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
    /// Decrements the refcount at the given handle. If it reaches zero, the handle is added to the cascade
    /// pending stack. If no cascade is already active, starts the cascade loop which pops pending handles and
    /// calls <see cref="IRefCountHandler.OnFreed"/> for each. The handler is responsible for decrementing
    /// child refcounts (by calling <see cref="Decrement{THandler}"/> for each child) and freeing the node.
    ///
    /// Re-entrant calls (from within a handler, or from cross-table cascades) push onto the existing pending
    /// stack instead of starting a nested loop, keeping call-stack depth bounded.
    /// </summary>
    public void Decrement<THandler>(Handle<T> handle, THandler handler)
        where THandler : struct, IRefCountHandler
    {
        this.AssertOwnerThread();
        Debug.Assert(_directory[handle.Index] > 0, $"Decrement on index {handle.Index} with refcount already at {_directory[handle.Index]}.");

        if (--_directory[handle.Index] != 0)
            return;

        _cascadePending.Push(handle);

        if (!_cascadeActive)
            this.RunCascade(handler);
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
    /// Decrements refcounts for all handles in the span, then cascades any that hit zero. Must not be called
    /// during an active cascade (i.e., not re-entrant — batch operations are top-level coordinator calls).
    /// </summary>
    public void DecrementBatch<THandler>(ReadOnlySpan<Handle<T>> handles, THandler handler)
        where THandler : struct, IRefCountHandler
    {
        this.AssertOwnerThread();
        Debug.Assert(!_cascadeActive, "DecrementBatch must not be called during an active cascade.");

        for (var i = 0; i < handles.Length; i++)
        {
            var index = handles[i].Index;
            Debug.Assert(_directory[index] > 0, $"DecrementBatch on index {index} with refcount already at {_directory[index]}.");
            if (--_directory[index] == 0)
                _cascadePending.Push(handles[i]);
        }

        this.RunCascade(handler);
    }

    // ── Cascade loop ─────────────────────────────────────────────────────

    private void RunCascade<THandler>(THandler handler)
        where THandler : struct, IRefCountHandler
    {
        if (_cascadePending.Count == 0)
            return;

        _cascadeActive = true;

        while (_cascadePending.TryPop(out var current))
            handler.OnFreed(current, this);

        _cascadeActive = false;
    }

    // ── Callback interface ───────────────────────────────────────────────

    /// <summary>
    /// Handles a node whose refcount reached zero. The implementation is responsible for both decrementing
    /// child refcounts (by calling <see cref="Decrement{THandler}"/> for each child) and freeing the node
    /// (e.g., returning the index to the arena's free list). The <paramref name="table"/> parameter enables
    /// the handler to call <see cref="Decrement{THandler}"/> for child handles.
    /// </summary>
    public interface IRefCountHandler
    {
        void OnFreed(Handle<T> handle, RefCountTable<T> table);
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
        public bool CascadeActive => table._cascadeActive;
    }
}
