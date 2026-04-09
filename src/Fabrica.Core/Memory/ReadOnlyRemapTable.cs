using System.Runtime.CompilerServices;
#if UNSAFE_OPT
using System.Runtime.InteropServices;
#endif
using Fabrica.Core.Collections.Unsafe;

namespace Fabrica.Core.Memory;

/// <summary>
/// Read-only view of a <see cref="NonCopyableRemapTable"/>. Supports <see cref="Resolve"/> and
/// <see cref="Remap{T}"/> but cannot mutate the underlying data. Safe to copy by value.
///
/// Use <see cref="NonCopyableRemapTable.AsReadOnly"/> to obtain an instance.
/// </summary>
internal readonly struct ReadOnlyRemapTable(NonCopyableUnsafeList<int> data, int threadCount)
{
    public int ThreadCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => threadCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Resolve(int threadId, int localIndex)
    {
#if UNSAFE_OPT
        ref var r0 = ref MemoryMarshal.GetArrayDataReference(data.UnsafeBackingArray);
        var baseOffset = Unsafe.Add(ref r0, threadId);
        return Unsafe.Add(ref r0, baseOffset + localIndex);
#else
        var span = data.WrittenSpan;
        return span[span[threadId] + localIndex];
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Handle<T> Remap<T>(Handle<T> handle) where T : struct
    {
        var index = handle.Index;
        if (!TaggedHandle.IsLocal(index))
            return handle;

        var threadId = TaggedHandle.DecodeThreadId(index);
        var localIndex = TaggedHandle.DecodeLocalIndex(index);
        return new Handle<T>(this.Resolve(threadId, localIndex));
    }
}
