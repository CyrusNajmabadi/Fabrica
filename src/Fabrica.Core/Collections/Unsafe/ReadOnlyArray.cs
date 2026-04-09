using System.Runtime.CompilerServices;

namespace Fabrica.Core.Collections.Unsafe;

/// <summary>
/// Immutable snapshot of a <see cref="NonCopyableUnsafeList{T}"/>: a <typeparamref name="T"/>[] reference
/// plus a frozen count. Safe to copy — both fields are readonly and the backing array is never
/// mutated through this type.
///
/// Use <see cref="NonCopyableUnsafeList{T}.AsReadOnly"/> to obtain an instance.
/// Use <see cref="DetachArray"/> to reclaim the backing array for pooling.
/// </summary>
internal readonly struct ReadOnlyArray<T>(T[] array, int count)
{
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => count;
    }

    public ReadOnlySpan<T> AsSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(array, 0, count);
    }

    /// <summary>Returns the raw backing array for recycling into an object pool.</summary>
    internal T[] DetachArray() => array;
}
