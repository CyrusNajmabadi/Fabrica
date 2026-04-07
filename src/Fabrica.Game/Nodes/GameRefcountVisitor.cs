using System.Runtime.CompilerServices;
using Fabrica.Core.Memory;

namespace Fabrica.Game.Nodes;

/// <summary>
/// Increments child refcounts after handle rewriting. For each child handle visited, the visitor
/// increments the refcount in the appropriate <see cref="GlobalNodeStore{TNode,TNodeOps}"/>.
/// </summary>
internal struct GameRefcountVisitor : INodeVisitor
{
    internal GlobalNodeStore<MachineNode, GameNodeOps> MachineStore;
    internal GlobalNodeStore<BeltSegmentNode, GameNodeOps> BeltStore;
    internal GlobalNodeStore<ItemNode, GameNodeOps> ItemStore;

    public readonly void Visit<T>(Handle<T> handle) where T : struct
    {
        if (typeof(T) == typeof(MachineNode))
            MachineStore.IncrementRefCount(Unsafe.As<Handle<T>, Handle<MachineNode>>(ref handle));
        else if (typeof(T) == typeof(BeltSegmentNode))
            BeltStore.IncrementRefCount(Unsafe.As<Handle<T>, Handle<BeltSegmentNode>>(ref handle));
        else if (typeof(T) == typeof(ItemNode))
            ItemStore.IncrementRefCount(Unsafe.As<Handle<T>, Handle<ItemNode>>(ref handle));
    }
}
