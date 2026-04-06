using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.Game.Nodes;

namespace Fabrica.Game.Jobs;

/// <summary>
/// Depends on <see cref="BuildBeltChainJob"/>. Creates a <see cref="MachineNode"/> wired to the
/// belt chain's head and tail, and marks it as a root. All belt segments and items become
/// reachable through this machine root.
/// </summary>
internal sealed class PlaceMachinesJob : Job
{
    internal ThreadLocalBuffer<MachineNode>[]? MachineTlbs;
    internal BuildBeltChainJob? BeltJob;

    protected override void Execute(WorkerContext context)
    {
        var tlb = MachineTlbs![context.WorkerIndex];
        var handle = tlb.Allocate(isRoot: true);
        var localIndex = TaggedHandle.DecodeLocalIndex(handle.Index);
        tlb[localIndex] = new MachineNode
        {
            InputBelt = BeltJob!.ChainHead,
            OutputBelt = BeltJob.ChainTail,
            RecipeId = 1,
            Progress = 0,
        };
    }

    protected override void Reset()
    {
        MachineTlbs = null;
        BeltJob = null;
    }
}
