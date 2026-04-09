using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.SampleGame.Nodes;

namespace Fabrica.SampleGame.Jobs;

/// <summary>
/// Depends on <see cref="BuildBeltChainJob"/>. Creates a <see cref="MachineNode"/> wired to the
/// belt chain's head and tail, and marks it as a root. All belt segments and items become
/// reachable through this machine root.
/// </summary>
internal sealed class PlaceMachinesJob : Job
{
    internal ThreadLocalBuffer<MachineNode>[]? MachineThreadLocalBuffers;

    private BuildBeltChainJob? _beltJob;

    internal BuildBeltChainJob? BeltJob
    {
        get => _beltJob;
        set
        {
            _beltJob = value;
            if (value != null) this.DependsOn(value);
        }
    }

    protected override void Execute(JobContext context)
    {
        ref var threadLocalBuffer = ref MachineThreadLocalBuffers![context.WorkerIndex];
        var handle = threadLocalBuffer.Allocate(isRoot: true);
        threadLocalBuffer[handle] = new MachineNode
        {
            InputBelt = _beltJob!.ChainHead,
            OutputBelt = _beltJob.ChainTail,
            RecipeId = 1,
            Progress = 0,
        };
    }

    protected override void ResetState()
    {
        MachineThreadLocalBuffers = null;
        _beltJob = null;
    }
}
