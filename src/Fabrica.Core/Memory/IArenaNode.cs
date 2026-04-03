namespace Fabrica.Core.Memory;

/// <summary>
/// Contract for struct node types stored in a <see cref="UnsafeSlabArena{T}"/>. Enables the
/// <see cref="ArenaCoordinator{TNode}"/> to remap tagged local indices to global indices during merge
/// and to establish refcounts for child references.
///
/// Implementations must be structs. The coordinator calls these methods via a constrained generic type
/// parameter (<c>where TNode : struct, IArenaNode</c>), so the JIT specializes per concrete type —
/// no interface dispatch, no boxing, full inlining.
/// </summary>
internal interface IArenaNode
{
    /// <summary>
    /// Rewrites any child-reference fields that contain tagged local indices (see <see cref="ArenaIndex.IsLocal"/>)
    /// to global arena indices using the provided mapping. Fields that are already global or equal to
    /// <see cref="ArenaIndex.NoChild"/> must be left unchanged.
    /// </summary>
    /// <param name="localToGlobalMap">Maps local buffer index → global arena index for the originating buffer.</param>
    void FixupReferences(ReadOnlySpan<int> localToGlobalMap);

    /// <summary>
    /// Increments the refcount for every valid child of this node. Called by the coordinator after fixup, when all
    /// child fields contain global indices. Fields equal to <see cref="ArenaIndex.NoChild"/> must be skipped.
    /// </summary>
    void IncrementChildren(RefCountTable table);
}
