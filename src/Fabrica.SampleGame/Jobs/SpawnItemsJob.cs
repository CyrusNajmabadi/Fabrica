using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.SampleGame.Nodes;

namespace Fabrica.SampleGame.Jobs;

/// <summary>
/// Root of the job DAG (no dependencies). Allocates <see cref="ItemNode"/> instances in the
/// executing worker's TLB. Downstream jobs read <see cref="AllocatedItems"/> to wire items
/// into belt segments.
/// </summary>
internal sealed class SpawnItemsJob(JobScheduler scheduler) : Job(scheduler)
{
    internal ThreadLocalBuffer<ItemNode>[]? ItemThreadLocalBuffers;
    internal int Count;
    internal Handle<ItemNode>[]? AllocatedItems;

    protected override void Execute(JobContext context)
    {
        ref var threadLocalBuffer = ref ItemThreadLocalBuffers![context.WorkerIndex];
        if (AllocatedItems is null || AllocatedItems.Length < Count)
            AllocatedItems = new Handle<ItemNode>[Count];
        for (var i = 0; i < Count; i++)
        {
            AllocatedItems[i] = threadLocalBuffer.Allocate(new ItemNode { ItemTypeId = i % 4 });
        }
    }

    protected override void ResetState() => ItemThreadLocalBuffers = null;
}
