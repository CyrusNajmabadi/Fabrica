namespace Fabrica.Core.Memory;

/// <summary>
/// Strongly-typed index wrapper that prevents accidental use of an index from one arena/table
/// with a different arena/table. <typeparamref name="T"/> is the node struct type this handle
/// refers to — it is never stored, only used for compile-time discrimination.
///
/// The raw index and constructor are internal — external consumers see only <see cref="IsValid"/>,
/// <see cref="None"/>, and equality operators. Low-level index access is confined to Fabrica.Core
/// internals (arena, ref-count table, tagged-handle helpers, remap table).
/// </summary>
public readonly struct Handle<T> : IEquatable<Handle<T>> where T : struct
{
    public static readonly Handle<T> None = new(-1);

    internal Handle(int index) => this.Index = index;

    internal int Index { get; }

    public bool IsValid => this.Index >= 0;

    public bool Equals(Handle<T> other) => this.Index == other.Index;

    public override bool Equals(object? obj) => obj is Handle<T> other && this.Equals(other);

    public override int GetHashCode() => this.Index;

    public static bool operator ==(Handle<T> left, Handle<T> right) => left.Index == right.Index;

    public static bool operator !=(Handle<T> left, Handle<T> right) => left.Index != right.Index;

    public override string ToString() => $"Handle<{typeof(T).Name}>({this.Index})";
}
