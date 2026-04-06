namespace Fabrica.Core.Memory;

/// <summary>
/// Strongly-typed index wrapper that prevents accidental use of an index from one arena/table
/// with a different arena/table. <typeparamref name="T"/> is the node struct type this handle
/// refers to — it is never stored, only used for compile-time discrimination.
/// </summary>
public readonly struct Handle<T>(int index) : IEquatable<Handle<T>> where T : struct
{
    public static readonly Handle<T> None = new(-1);

    public int Index { get; } = index;

    public bool IsValid => this.Index >= 0;

    public bool Equals(Handle<T> other) => this.Index == other.Index;

    public override bool Equals(object? obj) => obj is Handle<T> other && this.Equals(other);

    public override int GetHashCode() => this.Index;

    public static bool operator ==(Handle<T> left, Handle<T> right) => left.Index == right.Index;

    public static bool operator !=(Handle<T> left, Handle<T> right) => left.Index != right.Index;

    public override string ToString() => $"Handle<{typeof(T).Name}>({this.Index})";
}
