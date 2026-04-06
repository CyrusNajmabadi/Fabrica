using System.Runtime.InteropServices;
using Fabrica.Core.Memory;

namespace Fabrica.Game.Nodes;

/// <summary>
/// A processing machine with input and output belts. References <see cref="BeltSegmentNode"/>
/// heads (cross-type). Machines are the only root nodes in the game DAG — all belt segments
/// and items are reachable through them.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MachineNode
{
    public Handle<BeltSegmentNode> InputBelt;
    public Handle<BeltSegmentNode> OutputBelt;
    public int RecipeId;
    public int Progress;
}
