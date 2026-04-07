using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.Game.Nodes;

namespace Fabrica.Game.Jobs;

/// <summary>
/// Root of the job DAG (no dependencies). Allocates <see cref="ItemNode"/> instances in the
/// executing worker's TLB. Downstream jobs read <see cref="AllocatedItems"/> to wire items
/// into belt segments.
/// </summary>
internal sealed class SpawnItemsJob : Job
{
    internal ThreadLocalBuffer<ItemNode>[]? ItemTlbs;
    internal int Count;
    internal Handle<ItemNode>[]? AllocatedItems;

    protected override void Execute(WorkerContext context)
    {
        var tlb = ItemTlbs![context.WorkerIndex];
        AllocatedItems = new Handle<ItemNode>[Count];
        for (var i = 0; i < Count; i++)
        {
            var handle = tlb.Allocate();
            var localIndex = TaggedHandle.DecodeLocalIndex(handle.Index);
            tlb[localIndex] = new ItemNode { ItemTypeId = i % 4 };
            AllocatedItems[i] = handle;
        }
    }

    protected override void Reset()
    {
        base.Reset();
        ItemTlbs = null;
        AllocatedItems = null;
    }
}
