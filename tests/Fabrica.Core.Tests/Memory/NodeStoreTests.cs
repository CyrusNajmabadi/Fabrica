using System.Runtime.InteropServices;
using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class NodeStoreTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TreeNode
    {
        public Handle<TreeNode> Left { get; set; }
        public Handle<TreeNode> Right { get; set; }
        public int Value { get; set; }
    }

    private struct TreeHandler(UnsafeSlabArena<TreeNode> arena, TreeChildEnumerator enumerator) : RefCountTable<TreeNode>.IRefCountHandler
    {
        public readonly void OnFreed(Handle<TreeNode> handle, RefCountTable<TreeNode> table)
        {
            ref readonly var node = ref arena[handle];
            var visitor = new RefCountTable<TreeNode>.DecrementNodeRefCountVisitor<TreeHandler>(table, this);
            enumerator.EnumerateChildren(in node, ref visitor);
            arena.Free(handle);
        }
    }

    private struct TreeChildEnumerator : INodeChildEnumerator<TreeNode>
    {
        public readonly void EnumerateChildren<TAction>(in TreeNode node, ref TAction visitor)
            where TAction : struct, INodeVisitor
        {
            if (node.Left.IsValid) visitor.Visit(node.Left);
            if (node.Right.IsValid) visitor.Visit(node.Right);
        }

        public readonly void EnumerateChildren<TAction, TContext>(in TreeNode node, in TContext context, ref TAction visitor)
            where TAction : struct, INodeVisitor<TContext>
        {
            if (node.Left.IsValid) visitor.Visit(node.Left, in context);
            if (node.Right.IsValid) visitor.Visit(node.Right, in context);
        }

        public readonly void RewriteChildren<TRewriter>(ref TreeNode node, ref TRewriter rewriter)
            where TRewriter : struct, INodeHandleRewriter
        {
            if (node.Left.Index != -1) { var h = node.Left; rewriter.Rewrite(ref h); node.Left = h; }
            if (node.Right.Index != -1) { var h = node.Right; rewriter.Rewrite(ref h); node.Right = h; }
        }
    }

    private static NodeStore<TreeNode, TreeHandler> CreateStore()
    {
        var arena = new UnsafeSlabArena<TreeNode>();
        var refCounts = new RefCountTable<TreeNode>();
        var enumerator = new TreeChildEnumerator();
        var handler = new TreeHandler(arena, enumerator);
        var store = new NodeStore<TreeNode, TreeHandler>(arena, refCounts, handler);
        store.EnableValidation(enumerator);
        return store;
    }

    private static Handle<TreeNode> AllocNode(
        NodeStore<TreeNode, TreeHandler> store,
        Handle<TreeNode> left,
        Handle<TreeNode> right,
        int value)
    {
        var handle = store.Arena.Allocate();
        store.Arena[handle] = new TreeNode { Left = left, Right = right, Value = value };
        if (left.IsValid) store.RefCounts.Increment(left);
        if (right.IsValid) store.RefCounts.Increment(right);
        store.RefCounts.EnsureCapacity(handle.Index + 1);
        return handle;
    }

    [Fact]
    public void IncrementRoots_BumpsRefcounts()
    {
        var store = CreateStore();
        var a = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 0);
        var b = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 0);
        var c = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 0);

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
        var a = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 0);

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
        var c = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 3);
        var d = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 4);
        var a = AllocNode(store, c, d, 1);
        var b = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 2);
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
        var b = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 2);
        var c = AllocNode(store, Handle<TreeNode>.None, Handle<TreeNode>.None, 3);
        var a = AllocNode(store, b, c, 1);
        var root1 = AllocNode(store, a, Handle<TreeNode>.None, 10);
        var root2 = AllocNode(store, a, Handle<TreeNode>.None, 20);

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
