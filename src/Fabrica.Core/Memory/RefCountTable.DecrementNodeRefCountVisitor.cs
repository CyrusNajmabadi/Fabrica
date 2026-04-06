using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Fabrica.Core.Memory;

internal sealed partial class RefCountTable<T>
{
    /// <summary>
    /// An <see cref="INodeVisitor"/> that decrements same-type children in this table.
    /// Cross-type children (where <c>typeof(TChild) != typeof(T)</c>) are silently ignored — the
    /// <c>typeof</c> check is a JIT constant and the dead branch is eliminated entirely.
    ///
    /// Callers (enumerators) are expected to only pass valid handles — <see cref="DecrementTyped"/>
    /// asserts this in debug builds.
    ///
    /// For handlers whose nodes reference multiple types, define a custom <see cref="INodeVisitor"/>
    /// that captures all necessary tables and dispatches with per-type <c>typeof</c> checks.
    /// </summary>
    internal struct DecrementNodeRefCountVisitor<THandler>(RefCountTable<T> table, THandler handler) : INodeVisitor
        where THandler : struct, IRefCountHandler
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Visit<TChild>(ref Handle<TChild> child) where TChild : struct
        {
            if (typeof(TChild) == typeof(T))
                this.DecrementTyped(Unsafe.As<Handle<TChild>, Handle<T>>(ref child));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly void DecrementTyped(Handle<T> child)
        {
            Debug.Assert(child.IsValid);
            table.Decrement(child, handler);
        }
    }
}
