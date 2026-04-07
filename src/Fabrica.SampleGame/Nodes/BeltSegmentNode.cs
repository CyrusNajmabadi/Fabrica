using System.Runtime.InteropServices;
using Fabrica.Core.Memory;

namespace Fabrica.SampleGame.Nodes;

/// <summary>
/// One segment of a transport belt. Forms a singly-linked chain via <see cref="Next"/> (same-type)
/// and may carry an <see cref="ItemNode"/> as <see cref="Payload"/> (cross-type).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct BeltSegmentNode
{
    public Handle<BeltSegmentNode> Next;
    public Handle<ItemNode> Payload;
}
