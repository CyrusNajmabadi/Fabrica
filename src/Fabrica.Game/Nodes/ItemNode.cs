using System.Runtime.InteropServices;

namespace Fabrica.Game.Nodes;

/// <summary>
/// Leaf node: a single item on a belt or in a machine's buffer. Has no children.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ItemNode
{
    public int ItemTypeId;
}
