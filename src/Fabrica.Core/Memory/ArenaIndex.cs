using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Fabrica.Core.Memory;

/// <summary>
/// Bit-level convention for distinguishing global arena indices from thread-local buffer indices within a single
/// <c>int</c> field. Workers store tagged local indices in child-reference fields of new nodes; the coordinator
/// remaps them to global indices during merge.
///
/// Encoding:
///   <c>-1</c>         — no child sentinel (constant <see cref="NoChild"/>).
///   <c>&gt;= 0</c>    — global arena index (used after merge, or for existing nodes).
///   <c>&lt; -1</c>     — tagged local index. The raw local index is stored in the lower 31 bits, with the sign bit
///                         set. Untag via <c>value &amp; 0x7FFF_FFFF</c>. The minimum tagged value is
///                         <c>int.MinValue</c> (local index 0); the maximum is <c>-2</c> (local index
///                         <c>0x7FFF_FFFE</c>). Local index <c>0x7FFF_FFFF</c> would produce <c>-1</c>, colliding
///                         with the sentinel — but a single buffer will never hold 2 billion entries.
/// </summary>
internal static class ArenaIndex
{
    /// <summary>Sentinel value meaning "no child" / "null reference".</summary>
    public const int NoChild = -1;

    private const int TagBit = unchecked((int)0x80000000);

    /// <summary>Tags a local buffer index so it can be stored in a child-reference field alongside global indices.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int TagLocal(int localIndex)
    {
        Debug.Assert(localIndex >= 0, $"Local index must be non-negative, got {localIndex}.");
        return localIndex | TagBit;
    }

    /// <summary>Returns <c>true</c> if the value is a tagged local index (less than -1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLocal(int value)
        => value < NoChild;

    /// <summary>Returns <c>true</c> if the value is a global arena index (non-negative).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsGlobal(int value)
        => value >= 0;

    /// <summary>Extracts the raw local index from a tagged value. The caller must ensure <see cref="IsLocal"/> is
    /// <c>true</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int UntagLocal(int taggedValue)
    {
        Debug.Assert(IsLocal(taggedValue), $"Expected tagged local index (< -1), got {taggedValue}.");
        return taggedValue & 0x7FFF_FFFF;
    }
}
