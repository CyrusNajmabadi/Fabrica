using System.Runtime.CompilerServices;
#if UNSAFE_OPT
using System.Runtime.InteropServices;
#endif

namespace Fabrica.Core.Memory;

/// <summary>
/// Two-level array directory with O(1) indexed access to value-type entries. When <c>UNSAFE_OPT</c> is defined,
/// indexing uses <see cref="Unsafe.Add{T}"/> to bypass bounds checking. Otherwise, standard array access is
/// used so the CLR performs real bounds checking.
///
/// The directory is a pre-allocated <c>T[][]</c> where each inner array (slab) has a power-of-2 length, enabling
/// bit-shift and bitwise-AND to compute <c>(slabIndex, offset)</c> from a flat index.
///
/// Slabs are allocated on demand when <see cref="EnsureSlab"/> is called.
/// </summary>
internal sealed class UnsafeSlabDirectory<T>(int directoryLength, int slabShift) where T : struct
{
    private readonly T[][] _array = new T[directoryLength][];
    private readonly int _slabLength = 1 << slabShift;
    private readonly int _slabShift = slabShift;
    private readonly int _slabMask = (1 << slabShift) - 1;

    public int DirectoryLength => _array.Length;
    public int SlabLength => _slabLength;
    public int SlabShift => _slabShift;
    public int SlabMask => _slabMask;

    /// <summary>Raw directory array, exposed for tests only.</summary>
    internal T[][] RawArray => _array;

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var slabIndex = index >> _slabShift;
            var offset = index & _slabMask;

#if UNSAFE_OPT
            ref var slab = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_array), slabIndex);
            return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(slab!), offset);
#else
            return ref _array[slabIndex][offset];
#endif
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureSlab(int index)
    {
        var slabIndex = index >> _slabShift;

#if UNSAFE_OPT
        ref var slab = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_array), slabIndex);
        slab ??= new T[_slabLength];
#else
        _array[slabIndex] ??= new T[_slabLength];
#endif
    }
}
