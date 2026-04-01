using Fabrica.Engine.World;
using Fabrica.Pipeline;
using Fabrica.Tests.Helpers;
using Xunit;

namespace Fabrica.Tests.World;

using ChainNode = BaseProductionLoop<WorldImage>.ChainNode;

/// <summary>
/// Tests for <see cref="ChainNode"/> mechanics, exercised through <see cref="BaseProductionLoop{WorldImage}.ChainTestAccessor"/>
/// .
/// </summary>
public sealed class ChainNodeTests
{
    private readonly TestChainHarness _harness = new();
    private BaseProductionLoop<WorldImage>.ChainTestAccessor Accessor => _harness.GetChainTestAccessor();

    private ChainNode CreateNode(int sequenceNumber, WorldImage? payload = null)
    {
        var node = this.Accessor.CreateNode(sequenceNumber);
        this.Accessor.SetPayload(node, payload ?? new WorldImage());
        return node;
    }

    [Fact]
    public void InitializeBase_SetsSequenceNumberAndClearsChainState()
    {
        var node = this.CreateNode(7);

        Assert.Equal(7, node.SequenceNumber);
        Assert.Equal(0, node.PublishTimeNanoseconds);
        Assert.Null(this.Accessor.GetNext(node));
        Assert.False(node.IsUnreferenced);
    }

    [Fact]
    public void SetNext_LinksNodes()
    {
        var first = this.CreateNode(0);
        var second = this.CreateNode(1);

        this.Accessor.LinkNodes(first, second);

        Assert.Same(second, this.Accessor.GetNext(first));
    }

    [Fact]
    public void ClearNext_RemovesLink()
    {
        var first = this.CreateNode(0);
        var second = this.CreateNode(1);
        this.Accessor.LinkNodes(first, second);

        this.Accessor.ClearNext(first);

        Assert.Null(this.Accessor.GetNext(first));
    }

    [Fact]
    public void MarkPublished_SetsPublishTime()
    {
        var node = this.CreateNode(0);

        this.Accessor.MarkPublished(node, 42_000_000);

        Assert.Equal(42_000_000, node.PublishTimeNanoseconds);
    }

    [Fact]
    public void Initialize_ClearsPublishTime()
    {
        var node = this.CreateNode(0);
        this.Accessor.MarkPublished(node, 999);
        this.Accessor.ClearPayload(node);
        this.Accessor.Release(node);

        var node2 = this.Accessor.CreateNode(1);
        this.Accessor.SetPayload(node2, new WorldImage());

        Assert.Equal(0, node2.PublishTimeNanoseconds);
    }

    [Fact]
    public void AddRef_PreventsReleaseFromReachingZero()
    {
        var node = this.CreateNode(5);

        this.Accessor.AddRef(node);
        this.Accessor.Release(node);

        Assert.False(node.IsUnreferenced);
    }

    [Fact]
    public void AddRef_ThenFullRelease_ReachesZero()
    {
        var node = this.CreateNode(5);

        this.Accessor.AddRef(node);
        this.Accessor.Release(node);
        this.Accessor.Release(node);

        Assert.True(node.IsUnreferenced);
    }

    [Fact]
    public void Release_ToZero_ClearsNextPointer()
    {
        var node = this.CreateNode(10);
        var next = this.CreateNode(11);
        this.Accessor.LinkNodes(node, next);

        this.Accessor.Release(node);

        Assert.True(node.IsUnreferenced);
        Assert.Null(this.Accessor.GetNext(node));
    }

    [Fact]
    public void ClearPayload_NullsPayload()
    {
        var node = this.CreateNode(5);

        this.Accessor.ClearPayload(node);

        Assert.Null(node.Payload);
    }

    // ── Chain iterator ──────────────────────────────────────────────

    [Fact]
    public void Chain_SingleNode_YieldsOneElement()
    {
        var node = this.CreateNode(0);

        var ticks = new List<int>();
        foreach (var n in ChainNode.Chain(null, node))
            ticks.Add(n.SequenceNumber);

        Assert.Equal([0], ticks);
    }

    [Fact]
    public void Chain_MultipleNodes_YieldsAllInOrder()
    {
        var a = this.CreateNode(1);
        var b = this.CreateNode(2);
        var c = this.CreateNode(3);
        this.Accessor.LinkNodes(a, b);
        this.Accessor.LinkNodes(b, c);

        var ticks = new List<int>();
        foreach (var n in ChainNode.Chain(a, c))
            ticks.Add(n.SequenceNumber);

        Assert.Equal([1, 2, 3], ticks);
    }

    [Fact]
    public void Chain_StopsAtEnd_DoesNotReadBeyond()
    {
        var a = this.CreateNode(1);
        var b = this.CreateNode(2);
        var c = this.CreateNode(3);
        this.Accessor.LinkNodes(a, b);
        this.Accessor.LinkNodes(b, c);

        var ticks = new List<int>();
        foreach (var n in ChainNode.Chain(a, b))
            ticks.Add(n.SequenceNumber);

        Assert.Equal([1, 2], ticks);
    }

    [Fact]
    public void Chain_NullStart_YieldsOnlyEnd()
    {
        var node = this.CreateNode(5);

        var ticks = new List<int>();
        foreach (var n in ChainNode.Chain(null, node))
            ticks.Add(n.SequenceNumber);

        Assert.Equal([5], ticks);
    }
}
