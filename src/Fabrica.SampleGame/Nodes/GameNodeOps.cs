using System.Runtime.CompilerServices;
using Fabrica.Core.Memory;
using Fabrica.Core.Memory.Nodes;

namespace Fabrica.SampleGame.Nodes;

/// <summary>
/// Single struct implementing <see cref="INodeOps{TNode}"/> for all three game node types.
/// Requires two-phase initialization: construct all three <see cref="GlobalNodeStore{TNode,TNodeOps}"/>
/// instances first, then call <see cref="GlobalNodeStore{TNode,TNodeOps}.SetNodeOps"/> on each
/// with an ops instance that captures all three store references.
/// </summary>
public struct GameNodeOps : INodeOps<MachineNode>, INodeOps<BeltSegmentNode>, INodeOps<ItemNode>
{
    internal GlobalNodeStore<MachineNode, GameNodeOps> MachineStore;
    internal GlobalNodeStore<BeltSegmentNode, GameNodeOps> BeltStore;
    internal GlobalNodeStore<ItemNode, GameNodeOps> ItemStore;

    // ── MachineNode ─────────────────────────────────────────────────────

    readonly void INodeOps<MachineNode>.EnumerateChildren<TVisitor>(in MachineNode node, ref TVisitor visitor)
    {
        visitor.Visit(node.InputBelt);
        visitor.Visit(node.OutputBelt);
    }

    readonly void INodeOps<MachineNode>.EnumerateRefChildren<TVisitor>(ref MachineNode node, ref TVisitor visitor)
    {
        visitor.VisitRef(ref node.InputBelt);
        visitor.VisitRef(ref node.OutputBelt);
    }

    // ── BeltSegmentNode ─────────────────────────────────────────────────

    readonly void INodeOps<BeltSegmentNode>.EnumerateChildren<TVisitor>(in BeltSegmentNode node, ref TVisitor visitor)
    {
        visitor.Visit(node.Next);
        visitor.Visit(node.Payload);
    }

    readonly void INodeOps<BeltSegmentNode>.EnumerateRefChildren<TVisitor>(ref BeltSegmentNode node, ref TVisitor visitor)
    {
        visitor.VisitRef(ref node.Next);
        visitor.VisitRef(ref node.Payload);
    }

    // ── ItemNode (leaf — no children) ───────────────────────────────────

    readonly void INodeOps<ItemNode>.EnumerateChildren<TVisitor>(in ItemNode node, ref TVisitor visitor)
    {
    }

    readonly void INodeOps<ItemNode>.EnumerateRefChildren<TVisitor>(ref ItemNode node, ref TVisitor visitor)
    {
    }

    // ── Increment child refcounts (merge phase 2b) ──────────────────────

    readonly void INodeOps<MachineNode>.IncrementChildRefCounts(in MachineNode node)
    {
        BeltStore.IncrementRefCount(node.InputBelt);
        BeltStore.IncrementRefCount(node.OutputBelt);
    }

    readonly void INodeOps<BeltSegmentNode>.IncrementChildRefCounts(in BeltSegmentNode node)
    {
        BeltStore.IncrementRefCount(node.Next);
        ItemStore.IncrementRefCount(node.Payload);
    }

    readonly void INodeOps<ItemNode>.IncrementChildRefCounts(in ItemNode node)
    {
    }

    // ── Handle remapping (used during merge phase 2a) ──────────────────

    public readonly void VisitRef<T>(ref Handle<T> handle) where T : struct
    {
        if (typeof(T) == typeof(MachineNode))
            handle = MachineStore.RemapHandle(handle);
        else if (typeof(T) == typeof(BeltSegmentNode))
            handle = BeltStore.RemapHandle(handle);
        else if (typeof(T) == typeof(ItemNode))
            handle = ItemStore.RemapHandle(handle);
    }

    // ── Cascade dispatch ────────────────────────────────────────────────

    public readonly void Visit<T>(Handle<T> handle) where T : struct
    {
        if (typeof(T) == typeof(MachineNode))
        {
            MachineStore.DecrementRefCount(Unsafe.As<Handle<T>, Handle<MachineNode>>(ref handle));
        }
        else if (typeof(T) == typeof(BeltSegmentNode))
        {
            BeltStore.DecrementRefCount(Unsafe.As<Handle<T>, Handle<BeltSegmentNode>>(ref handle));
        }
        else if (typeof(T) == typeof(ItemNode))
        {
            ItemStore.DecrementRefCount(Unsafe.As<Handle<T>, Handle<ItemNode>>(ref handle));
        }
    }
}
