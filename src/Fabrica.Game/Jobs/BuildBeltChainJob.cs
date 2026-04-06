using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.Game.Nodes;

namespace Fabrica.Game.Jobs;

/// <summary>
/// Depends on <see cref="SpawnItemsJob"/>. Creates a singly-linked chain of
/// <see cref="BeltSegmentNode"/> segments, wiring each to the next and optionally carrying
/// an <see cref="ItemNode"/> from the spawn job. Built in reverse so each segment's
/// <see cref="BeltSegmentNode.Next"/> is known at construction time.
/// </summary>
internal sealed class BuildBeltChainJob : Job
{
    internal ThreadLocalBuffer<BeltSegmentNode>[]? BeltTlbs;
    internal SpawnItemsJob? SpawnJob;
    internal int ChainLength;

    internal Handle<BeltSegmentNode> ChainHead;
    internal Handle<BeltSegmentNode> ChainTail;

    protected override void Execute(WorkerContext context)
    {
        var tlb = BeltTlbs![context.WorkerIndex];
        var items = SpawnJob!.AllocatedItems!;

        var next = Handle<BeltSegmentNode>.None;

        for (var i = ChainLength - 1; i >= 0; i--)
        {
            var handle = tlb.Allocate();
            var localIndex = TaggedHandle.DecodeLocalIndex(handle.Index);
            var payload = i < items.Length ? items[i] : Handle<ItemNode>.None;
            tlb[localIndex] = new BeltSegmentNode { Next = next, Payload = payload };

            if (i == ChainLength - 1) ChainTail = handle;
            next = handle;
        }

        ChainHead = next;
    }

    protected override void Reset()
    {
        BeltTlbs = null;
        SpawnJob = null;
        ChainHead = default;
        ChainTail = default;
    }
}
