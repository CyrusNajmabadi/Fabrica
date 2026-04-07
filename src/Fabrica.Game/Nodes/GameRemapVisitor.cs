using Fabrica.Core.Memory;

namespace Fabrica.Game.Nodes;

/// <summary>
/// Rewrites local (tagged) handles to global arena indices during the merge. Each handle field
/// is resolved through the per-type <see cref="RemapTable"/> that
/// <see cref="GlobalNodeStore{TNode,TNodeOps}.DrainBuffers"/> populated.
/// </summary>
internal struct GameRemapVisitor : INodeVisitor
{
    internal RemapTable MachineRemap;
    internal RemapTable BeltRemap;
    internal RemapTable ItemRemap;

    public readonly void VisitRef<T>(ref Handle<T> handle) where T : struct
    {
        if (typeof(T) == typeof(MachineNode))
            handle = MachineRemap.Remap(handle);
        else if (typeof(T) == typeof(BeltSegmentNode))
            handle = BeltRemap.Remap(handle);
        else if (typeof(T) == typeof(ItemNode))
            handle = ItemRemap.Remap(handle);
    }
}
