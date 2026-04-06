using System.Runtime.InteropServices;
using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class HandleRewriterTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TreeNode
    {
        public Handle<TreeNode> Left;
        public Handle<TreeNode> Right;
        public int Value;
    }

    private struct TreeChildEnumerator : INodeChildEnumerator<TreeNode>
    {
        public readonly void EnumerateChildren<TVisitor>(in TreeNode node, ref TVisitor visitor)
            where TVisitor : struct, INodeVisitor
        {
            if (node.Left.IsValid) visitor.Visit(node.Left);
            if (node.Right.IsValid) visitor.Visit(node.Right);
        }

        public readonly void EnumerateChildren<TVisitor, TContext>(in TreeNode node, in TContext context, ref TVisitor visitor)
            where TVisitor : struct, INodeVisitor<TContext>
        {
            if (node.Left.IsValid) visitor.Visit(node.Left, in context);
            if (node.Right.IsValid) visitor.Visit(node.Right, in context);
        }

        public readonly void EnumerateRefChildren<TVisitor>(ref TreeNode node, ref TVisitor visitor)
            where TVisitor : struct, INodeVisitor
        {
            if (node.Left.Index != -1) visitor.VisitRef(ref node.Left);
            if (node.Right.Index != -1) visitor.VisitRef(ref node.Right);
        }
    }

    /// <summary>
    /// Simple visitor that maps local handles to global handles using a flat remap array.
    /// </summary>
    private struct TestVisitor(int[] remap) : INodeVisitor
    {
        public readonly void Visit<TChild>(Handle<TChild> child) where TChild : struct
            => throw new NotSupportedException();

        public readonly void VisitRef<TChild>(ref Handle<TChild> child) where TChild : struct
        {
            var index = child.Index;
            if (TaggedHandle.IsLocal(index))
            {
                var localIndex = TaggedHandle.DecodeLocalIndex(index);
                child = new Handle<TChild>(remap[localIndex]);
            }
        }
    }

    // ═══════════════════════════ Rewrite local → global ══════════════════

    [Fact]
    public void RewriteChildren_LocalToGlobal()
    {
        var localLeft = TaggedHandle.EncodeLocal(threadId: 0, localIndex: 0);
        var localRight = TaggedHandle.EncodeLocal(threadId: 0, localIndex: 1);

        var node = new TreeNode
        {
            Left = new Handle<TreeNode>(localLeft),
            Right = new Handle<TreeNode>(localRight),
            Value = 42,
        };

        int[] remap = [100, 200];
        var visitor = new TestVisitor(remap);
        var enumerator = new TreeChildEnumerator();

        enumerator.EnumerateRefChildren(ref node, ref visitor);

        Assert.Equal(100, node.Left.Index);
        Assert.Equal(200, node.Right.Index);
        Assert.True(TaggedHandle.IsGlobal(node.Left.Index));
        Assert.True(TaggedHandle.IsGlobal(node.Right.Index));
        Assert.Equal(42, node.Value);
    }

    [Fact]
    public void RewriteChildren_GlobalHandlesUntouched()
    {
        var node = new TreeNode
        {
            Left = new Handle<TreeNode>(50),
            Right = new Handle<TreeNode>(60),
        };

        int[] remap = [999];
        var visitor = new TestVisitor(remap);
        var enumerator = new TreeChildEnumerator();

        enumerator.EnumerateRefChildren(ref node, ref visitor);

        Assert.Equal(50, node.Left.Index);
        Assert.Equal(60, node.Right.Index);
    }

    [Fact]
    public void RewriteChildren_NoneHandlesUntouched()
    {
        var node = new TreeNode
        {
            Left = Handle<TreeNode>.None,
            Right = Handle<TreeNode>.None,
        };

        int[] remap = [999];
        var visitor = new TestVisitor(remap);
        var enumerator = new TreeChildEnumerator();

        enumerator.EnumerateRefChildren(ref node, ref visitor);

        Assert.Equal(Handle<TreeNode>.None, node.Left);
        Assert.Equal(Handle<TreeNode>.None, node.Right);
    }

    [Fact]
    public void RewriteChildren_MixedLocalAndGlobal()
    {
        var localHandle = TaggedHandle.EncodeLocal(threadId: 3, localIndex: 5);

        var node = new TreeNode
        {
            Left = new Handle<TreeNode>(localHandle),
            Right = new Handle<TreeNode>(42),
        };

        var remap = new int[6];
        remap[5] = 300;

        var visitor = new TestVisitor(remap);
        var enumerator = new TreeChildEnumerator();

        enumerator.EnumerateRefChildren(ref node, ref visitor);

        Assert.Equal(300, node.Left.Index);
        Assert.Equal(42, node.Right.Index);
    }

    // ═══════════════════════════ End-to-end with TLB ═════════════════════

    [Fact]
    public void EndToEnd_TLB_Allocate_Rewrite()
    {
        var tlb = new ThreadLocalBuffer<TreeNode>(threadId: 2);

        var leafHandle = tlb.Allocate();
        var leafIndex = TaggedHandle.DecodeLocalIndex(leafHandle.Index);
        tlb[leafIndex] = new TreeNode { Value = 10, Left = Handle<TreeNode>.None, Right = Handle<TreeNode>.None };

        var parentHandle = tlb.Allocate();
        var parentIndex = TaggedHandle.DecodeLocalIndex(parentHandle.Index);
        tlb[parentIndex] = new TreeNode { Value = 20, Left = leafHandle, Right = Handle<TreeNode>.None };

        int[] remap = [500, 501];
        var visitor = new TestVisitor(remap);
        var enumerator = new TreeChildEnumerator();

        var parent = tlb[parentIndex];
        enumerator.EnumerateRefChildren(ref parent, ref visitor);

        Assert.Equal(500, parent.Left.Index);
        Assert.True(TaggedHandle.IsGlobal(parent.Left.Index));
        Assert.Equal(Handle<TreeNode>.None, parent.Right);
        Assert.Equal(20, parent.Value);
    }
}
