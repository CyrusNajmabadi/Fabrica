using System.Runtime.InteropServices;
using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class NodeStoreTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TreeNode
    {
        public int Left { get; set; }
        public int Right { get; set; }
        public int Value { get; set; }
    }

    private struct TreeHandler(UnsafeSlabArena<TreeNode> arena) : RefCountTable.IRefCountHandler
    {
        public readonly void OnFreed(int index, RefCountTable table)
        {
            ref readonly var node = ref arena[index];
            if (node.Left >= 0) table.Decrement(node.Left, this);
            if (node.Right >= 0) table.Decrement(node.Right, this);
            arena.Free(index);
        }
    }

    private struct TreeChildEnumerator : DagValidator.IChildEnumerator<TreeNode>
    {
        public readonly void GetChildren(in TreeNode node, List<int> children)
        {
            if (node.Left >= 0) children.Add(node.Left);
            if (node.Right >= 0) children.Add(node.Right);
        }
    }

    private static NodeStore<TreeNode, TreeHandler> CreateStore()
    {
        var arena = new UnsafeSlabArena<TreeNode>();
        var refCounts = new RefCountTable();
        var handler = new TreeHandler(arena);
        var store = new NodeStore<TreeNode, TreeHandler>(arena, refCounts, handler);
        store.EnableValidation(new TreeChildEnumerator());
        return store;
    }

    private static int AllocNode(NodeStore<TreeNode, TreeHandler> store, int left, int right, int value)
    {
        var index = store.Arena.Allocate();
        store.Arena[index] = new TreeNode { Left = left, Right = right, Value = value };
        if (left >= 0) store.RefCounts.Increment(left);
        if (right >= 0) store.RefCounts.Increment(right);
        store.RefCounts.EnsureCapacity(index + 1);
        return index;
    }

    [Fact]
    public void IncrementRoots_BumpsRefcounts()
    {
        var store = CreateStore();
        var a = AllocNode(store, -1, -1, 0);
        var b = AllocNode(store, -1, -1, 0);
        var c = AllocNode(store, -1, -1, 0);

        Assert.Equal(0, store.RefCounts.GetCount(a));
        Assert.Equal(0, store.RefCounts.GetCount(b));
        Assert.Equal(0, store.RefCounts.GetCount(c));

        store.IncrementRoots([a, b, c]);

        Assert.Equal(1, store.RefCounts.GetCount(a));
        Assert.Equal(1, store.RefCounts.GetCount(b));
        Assert.Equal(1, store.RefCounts.GetCount(c));
    }

    [Fact]
    public void IncrementRoots_MultipleTimesStacksRefcounts()
    {
        var store = CreateStore();
        var a = AllocNode(store, -1, -1, 0);

        store.IncrementRoots([a]);
        store.IncrementRoots([a]);
        store.IncrementRoots([a]);

        Assert.Equal(3, store.RefCounts.GetCount(a));
    }

    [Fact]
    public void DecrementRoots_CascadesFreesEntireTree()
    {
        var store = CreateStore();
        store.RefCounts.EnsureCapacity(10);

        //     root
        //    /    \
        //   a      b
        //  / \
        // c   d
        var c = AllocNode(store, -1, -1, 3);
        var d = AllocNode(store, -1, -1, 4);
        var a = AllocNode(store, c, d, 1);
        var b = AllocNode(store, -1, -1, 2);
        var root = AllocNode(store, a, b, 0);

        store.IncrementRoots([root]);

        Assert.Equal(1, store.RefCounts.GetCount(root));
        Assert.Equal(1, store.RefCounts.GetCount(a));
        Assert.Equal(1, store.RefCounts.GetCount(b));
        Assert.Equal(1, store.RefCounts.GetCount(c));
        Assert.Equal(1, store.RefCounts.GetCount(d));

        store.DecrementRoots([root]);

        Assert.Equal(0, store.RefCounts.GetCount(root));
        Assert.Equal(0, store.RefCounts.GetCount(a));
        Assert.Equal(0, store.RefCounts.GetCount(b));
        Assert.Equal(0, store.RefCounts.GetCount(c));
        Assert.Equal(0, store.RefCounts.GetCount(d));
    }

    [Fact]
    public void DecrementRoots_SharedSubtreesSurvive()
    {
        var store = CreateStore();
        store.RefCounts.EnsureCapacity(10);

        //  root1    root2
        //    |        |
        //    a ------/
        //   / \
        //  b   c
        var b = AllocNode(store, -1, -1, 2);
        var c = AllocNode(store, -1, -1, 3);
        var a = AllocNode(store, b, c, 1);
        var root1 = AllocNode(store, a, -1, 10);
        var root2 = AllocNode(store, a, -1, 20);

        store.IncrementRoots([root1, root2]);

        Assert.Equal(2, store.RefCounts.GetCount(a));
        Assert.Equal(1, store.RefCounts.GetCount(b));
        Assert.Equal(1, store.RefCounts.GetCount(c));

        store.DecrementRoots([root1]);

        Assert.Equal(0, store.RefCounts.GetCount(root1));
        Assert.Equal(1, store.RefCounts.GetCount(a));
        Assert.Equal(1, store.RefCounts.GetCount(b));
        Assert.Equal(1, store.RefCounts.GetCount(c));

        store.DecrementRoots([root2]);

        Assert.Equal(0, store.RefCounts.GetCount(root2));
        Assert.Equal(0, store.RefCounts.GetCount(a));
        Assert.Equal(0, store.RefCounts.GetCount(b));
        Assert.Equal(0, store.RefCounts.GetCount(c));
    }

    [Fact]
    public void DecrementRoots_EmptySpanIsNoOp()
    {
        var store = CreateStore();
        store.DecrementRoots([]);
    }

    [Fact]
    public void IncrementRoots_EmptySpanIsNoOp()
    {
        var store = CreateStore();
        store.IncrementRoots([]);
    }
}
