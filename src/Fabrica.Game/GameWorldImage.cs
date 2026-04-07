using Fabrica.Core.Memory;
using Fabrica.Game.Nodes;

namespace Fabrica.Game;

/// <summary>
/// Game-specific payload: holds one <see cref="SnapshotSlice{TNode,TNodeOps}"/> per node type.
/// Published into the pipeline each tick, consumed by the renderer.
/// </summary>
public sealed class GameWorldImage
{
    public SnapshotSlice<MachineNode, GameNodeOps> MachineSlice;
    public SnapshotSlice<BeltSegmentNode, GameNodeOps> BeltSlice;
    public SnapshotSlice<ItemNode, GameNodeOps> ItemSlice;

    public void ResetForPool()
    {
        MachineSlice = default;
        BeltSlice = default;
        ItemSlice = default;
    }

    public readonly struct Allocator : IAllocator<GameWorldImage>
    {
        public readonly GameWorldImage Allocate() => new();

        public readonly void Reset(GameWorldImage item) => item.ResetForPool();
    }
}
