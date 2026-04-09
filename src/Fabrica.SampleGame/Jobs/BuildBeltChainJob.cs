using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.SampleGame.Nodes;

namespace Fabrica.SampleGame.Jobs;

/// <summary>
/// Depends on <see cref="SpawnItemsJob"/>. Creates a singly-linked chain of
/// <see cref="BeltSegmentNode"/> segments, wiring each to the next and optionally carrying
/// an <see cref="ItemNode"/> from the spawn job.
/// </summary>
internal sealed class BuildBeltChainJob : Job
{
    internal ThreadLocalBuffer<BeltSegmentNode>[]? BeltThreadLocalBuffers;
    internal int ChainLength;

    private SpawnItemsJob? _spawnJob;

    internal SpawnItemsJob? SpawnJob
    {
        get => _spawnJob;
        set
        {
            _spawnJob = value;
            if (value != null) this.DependsOn(value);
        }
    }

    internal Handle<BeltSegmentNode> ChainHead;
    internal Handle<BeltSegmentNode> ChainTail;

    protected override void Execute(JobContext context)
    {
        ref var threadLocalBuffer = ref BeltThreadLocalBuffers![context.WorkerIndex];
        var items = _spawnJob!.AllocatedItems!;

        // Built in reverse so each segment's Next handle is known at construction time.
        var next = Handle<BeltSegmentNode>.None;

        for (var i = ChainLength - 1; i >= 0; i--)
        {
            var payload = i < items.Length ? items[i] : Handle<ItemNode>.None;
            var handle = threadLocalBuffer.Allocate(new BeltSegmentNode { Next = next, Payload = payload });

            if (i == ChainLength - 1) ChainTail = handle;
            next = handle;
        }

        ChainHead = next;
    }

    protected override void ResetState()
    {
        BeltThreadLocalBuffers = null;
        _spawnJob = null;
        ChainHead = default;
        ChainTail = default;
    }
}
