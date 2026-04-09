using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Fabrica.Core.Memory;

/// <summary>
/// Static helpers for encoding and decoding tagged handle indices. During a parallel work phase,
/// workers create nodes in thread-local buffers and reference them via <b>local handles</b>.
/// After the join barrier, the coordinator rewrites local handles to <b>global handles</b> that
/// point into the shared <see cref="UnsafeSlabArena{T}"/>.
///
/// BIT LAYOUT
///   Global (bit 31 = 0, nonzero): [0][31 bits: global arena index]  — range 1 to 2,147,483,647
///   Local  (bit 31 = 1): [1][7 bits: thread ID][24 bits: local index]
///   None   (0x00000000): sentinel — neither global nor local
///
/// <see cref="Handle{T}"/> itself remains a clean <c>int</c> wrapper. These helpers interpret the
/// raw index contextually during work phases and the coordinator merge.
/// </summary>
internal static class TaggedHandle
{
    /// <summary>Maximum number of worker threads that can be encoded in a local handle.</summary>
    internal const int MaxThreads = 128;

    /// <summary>Maximum local index that fits in the 24-bit field (16,777,215).
    /// Full 24-bit range — no sentinel collision since None is 0, not -1.</summary>
    internal const int MaxLocalIndex = 0x00FF_FFFF;

    private const uint LocalBit = 0x8000_0000u;
    private const int ThreadIdShift = 24;
    private const int ThreadIdMask = 0x7F;
    private const int LocalIndexMask = 0x00FF_FFFF;

    /// <summary>Returns <c>true</c> if the index represents a global arena handle (bit 31 = 0, nonzero).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsGlobal(int index) => index > 0;

    /// <summary>Returns <c>true</c> if the index represents a local thread-buffer handle (bit 31 = 1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLocal(int index) => index < 0;

    /// <summary>Returns <c>true</c> if the index is the <see cref="Handle{T}.None"/> sentinel (0).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNone(int index) => index == 0;

    /// <summary>
    /// Encodes a local handle from a thread ID and local buffer index.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int EncodeLocal(int threadId, int localIndex)
    {
        Debug.Assert((uint)threadId < MaxThreads, $"Thread ID {threadId} exceeds maximum {MaxThreads - 1}.");
        Debug.Assert((uint)localIndex <= MaxLocalIndex, $"Local index {localIndex} exceeds maximum {MaxLocalIndex}.");
        return unchecked((int)(LocalBit | ((uint)threadId << ThreadIdShift) | (uint)localIndex));
    }

    /// <summary>Extracts the 7-bit thread ID from a local handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DecodeThreadId(int index)
    {
        Debug.Assert(IsLocal(index), $"DecodeThreadId called on non-local index {index}.");
        return (index >> ThreadIdShift) & ThreadIdMask;
    }

    /// <summary>Extracts the 24-bit local buffer index from a local handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DecodeLocalIndex(int index)
    {
        Debug.Assert(IsLocal(index), $"DecodeLocalIndex called on non-local index {index}.");
        return index & LocalIndexMask;
    }
}
