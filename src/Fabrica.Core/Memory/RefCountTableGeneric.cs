using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fabrica.Core.Threading;

namespace Fabrica.Core.Memory;

/// <summary>
/// Generic variant of <see cref="RefCountTable"/> parameterized on node type <typeparamref name="T"/>.
/// Identical logic, but public API uses <see cref="Handle{T}"/> instead of raw <c>int</c> to prevent
/// accidental cross-type index misuse. This is a temporary copy for benchmarking zero-overhead of the
/// generic + Handle wrapper pattern.
/// </summary>
internal sealed class RefCountTable<T> where T : struct
{
    private const int DefaultDirectoryLength = 65_536;

    private readonly UnsafeSlabDirectory<int> _directory;

    private readonly UnsafeStack<Handle<T>> _cascadePending = new();

    private bool _cascadeActive;

    private SingleThreadedOwner _owner;

    public RefCountTable()
        : this(DefaultDirectoryLength, SlabSizeHelper<int>.SlabShift)
    {
    }

    internal RefCountTable(int directoryLength, int slabShift)
        => _directory = new UnsafeSlabDirectory<int>(directoryLength, slabShift);

    public void EnsureCapacity(int highWater)
    {
        _owner.AssertOwnerThread();
        for (var slabStart = 0; slabStart < highWater; slabStart += _directory.SlabLength)
            _directory.EnsureSlab(slabStart);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetCount(Handle<T> handle)
        => _directory[handle.Index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Increment(Handle<T> handle)
    {
        _owner.AssertOwnerThread();
        _directory[handle.Index]++;
    }

    public void Decrement<THandler>(Handle<T> handle, THandler handler)
        where THandler : struct, IRefCountHandler
    {
        _owner.AssertOwnerThread();
        Debug.Assert(_directory[handle.Index] > 0, $"Decrement on index {handle.Index} with refcount already at {_directory[handle.Index]}.");

        if (--_directory[handle.Index] != 0)
            return;

        _cascadePending.Push(handle);

        if (!_cascadeActive)
            this.RunCascade(handler);
    }

    public void IncrementBatch(ReadOnlySpan<Handle<T>> handles)
    {
        _owner.AssertOwnerThread();
        for (var i = 0; i < handles.Length; i++)
            _directory[handles[i].Index]++;
    }

    public void DecrementBatch<THandler>(ReadOnlySpan<Handle<T>> handles, THandler handler)
        where THandler : struct, IRefCountHandler
    {
        _owner.AssertOwnerThread();
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

    public interface IRefCountHandler
    {
        void OnFreed(Handle<T> handle, RefCountTable<T> table);
    }

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
