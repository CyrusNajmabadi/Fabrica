using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Fabrica.Core.Memory;

/// <summary>
/// Two-level array directory with O(1) indexed access to value-type entries. In release builds, indexing uses
/// <see cref="Unsafe.Add{T}"/> to bypass bounds checking. In debug builds, full bounds assertions are performed.
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

            Debug.Assert((uint)slabIndex < (uint)_array.Length, $"Slab index {slabIndex} out of directory bounds [0, {_array.Length}).");
            ref var slab = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_array), slabIndex);

            Debug.Assert(slab is not null, $"Slab at index {slabIndex} is null.");
            Debug.Assert((uint)offset < (uint)slab!.Length, $"Offset {offset} out of slab bounds [0, {slab.Length}).");
            return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(slab), offset);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureSlab(int index)
    {
        var slabIndex = index >> _slabShift;

        Debug.Assert((uint)slabIndex < (uint)_array.Length, $"Slab index {slabIndex} out of directory bounds [0, {_array.Length}).");
        ref var slab = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_array), slabIndex);
        slab ??= new T[_slabLength];
    }
}
