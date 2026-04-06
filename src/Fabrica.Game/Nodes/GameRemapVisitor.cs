using Fabrica.Core.Memory;

namespace Fabrica.Game.Nodes;

/// <summary>
/// Rewrites local (tagged) handles to global arena indices during the merge. Each handle field
/// is decoded via <see cref="TaggedHandle"/> and resolved through the per-type <see cref="RemapTable"/>
/// that <see cref="GlobalNodeStore{TNode,TNodeOps}.DrainBuffers"/> populated.
/// </summary>
internal struct GameRemapVisitor : INodeVisitor
{
    internal RemapTable MachineRemap;
    internal RemapTable BeltRemap;
    internal RemapTable ItemRemap;

    public readonly void VisitRef<T>(ref Handle<T> handle) where T : struct
    {
        var index = handle.Index;
        if (!TaggedHandle.IsLocal(index))
            return;

        var threadId = TaggedHandle.DecodeThreadId(index);
        var localIndex = TaggedHandle.DecodeLocalIndex(index);

        if (typeof(T) == typeof(MachineNode))
            handle = new Handle<T>(MachineRemap.Resolve(threadId, localIndex));
        else if (typeof(T) == typeof(BeltSegmentNode))
            handle = new Handle<T>(BeltRemap.Resolve(threadId, localIndex));
        else if (typeof(T) == typeof(ItemNode))
            handle = new Handle<T>(ItemRemap.Resolve(threadId, localIndex));
    }
}
