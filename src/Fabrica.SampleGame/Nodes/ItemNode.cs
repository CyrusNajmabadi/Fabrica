using System.Runtime.InteropServices;

namespace Fabrica.SampleGame.Nodes;

/// <summary>
/// Leaf node: a single item on a belt or in a machine's buffer. Has no children.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ItemNode
{
    public int ItemTypeId;
}
