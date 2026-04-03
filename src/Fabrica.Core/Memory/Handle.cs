namespace Fabrica.Core.Memory;

/// <summary>
/// Strongly-typed index wrapper that prevents accidental use of an index from one arena/table
/// with a different arena/table. <typeparamref name="T"/> is the node struct type this handle
/// refers to — it is never stored, only used for compile-time discrimination.
/// </summary>
internal readonly struct Handle<T>(int index) where T : struct
{
    public static readonly Handle<T> None = new(-1);

    public int Index { get; } = index;

    public bool IsValid => this.Index >= 0;
}
