using System.Runtime.CompilerServices;

namespace Fabrica.Core.Memory;

/// <summary>
/// An <see cref="IChildAction"/> that decrements same-type children in a <see cref="RefCountTable{T}"/>.
/// Cross-type children (where <c>typeof(TChild) != typeof(TNode)</c>) are silently ignored — the
/// <c>typeof</c> check is a JIT constant and the dead branch is eliminated entirely.
///
/// For handlers whose nodes reference multiple types, define a custom <see cref="IChildAction"/>
/// that captures all necessary tables and dispatches with per-type <c>typeof</c> checks.
/// </summary>
internal struct DecrementChildAction<TNode, THandler>(RefCountTable<TNode> table, THandler handler) : IChildAction
    where TNode : struct
    where THandler : struct, RefCountTable<TNode>.IRefCountHandler
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void OnChild<TChild>(Handle<TChild> child) where TChild : struct
    {
        if (typeof(TChild) == typeof(TNode))
            this.DecrementTyped(Unsafe.As<Handle<TChild>, Handle<TNode>>(ref child));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly void DecrementTyped(Handle<TNode> child)
    {
        if (child.IsValid)
            table.Decrement(child, handler);
    }
}
