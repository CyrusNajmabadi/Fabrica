using Simulation.Engine;
using Simulation.World;
using Xunit;

namespace Simulation.Tests.World;

/// <summary>
/// Tests for <see cref="ChainNode{TPayload}"/> mechanics, exercised
/// through <see cref="ChainNode{WorldImage}"/>.
/// </summary>
public sealed class ChainNodeTests
{
    [Fact]
    public void InitializeBase_SetsSequenceNumberAndClearsChainState()
    {
        var node = new ChainNode<WorldImage>();
        node.InitializeBase(7);
        node.Payload = new WorldImage();

        Assert.Equal(7, node.SequenceNumber);
        Assert.Equal(0, node.PublishTimeNanoseconds);
        Assert.Null(node.NextInChain);
        Assert.False(node.IsUnreferenced);
    }

    [Fact]
    public void SetNext_LinksNodes()
    {
        var first = new ChainNode<WorldImage>();
        var second = new ChainNode<WorldImage>();

        first.InitializeBase(0);
        first.Payload = new WorldImage();
        second.InitializeBase(1);
        second.Payload = new WorldImage();

        first.SetNext(second);

        Assert.Same(second, first.NextInChain);
    }

    [Fact]
    public void ClearNext_RemovesLink()
    {
        var first = new ChainNode<WorldImage>();
        var second = new ChainNode<WorldImage>();

        first.InitializeBase(0);
        first.Payload = new WorldImage();
        second.InitializeBase(1);
        second.Payload = new WorldImage();
        first.SetNext(second);

        first.ClearNext();

        Assert.Null(first.NextInChain);
    }

    [Fact]
    public void MarkPublished_SetsPublishTime()
    {
        var node = new ChainNode<WorldImage>();
        node.InitializeBase(0);
        node.Payload = new WorldImage();

        node.MarkPublished(42_000_000);

        Assert.Equal(42_000_000, node.PublishTimeNanoseconds);
    }

    [Fact]
    public void Initialize_ClearsPublishTime()
    {
        var node = new ChainNode<WorldImage>();
        node.InitializeBase(0);
        node.Payload = new WorldImage();
        node.MarkPublished(999);
        node.ClearPayload();
        node.Release();

        node.InitializeBase(1);

        Assert.Equal(0, node.PublishTimeNanoseconds);
    }

    [Fact]
    public void AddRef_PreventsReleaseFromReachingZero()
    {
        var node = new ChainNode<WorldImage>();
        node.InitializeBase(5);
        node.Payload = new WorldImage();

        node.AddRef();
        node.Release();

        Assert.False(node.IsUnreferenced);
    }

    [Fact]
    public void AddRef_ThenFullRelease_ReachesZero()
    {
        var node = new ChainNode<WorldImage>();
        node.InitializeBase(5);
        node.Payload = new WorldImage();

        node.AddRef();
        node.Release();
        node.Release();

        Assert.True(node.IsUnreferenced);
    }

    [Fact]
    public void Release_ToZero_ClearsNextPointer()
    {
        var node = new ChainNode<WorldImage>();
        var next = new ChainNode<WorldImage>();

        node.InitializeBase(10);
        node.Payload = new WorldImage();
        next.InitializeBase(11);
        next.Payload = new WorldImage();
        node.SetNext(next);

        node.Release();

        Assert.True(node.IsUnreferenced);
        Assert.Null(node.NextInChain);
    }

    [Fact]
    public void ClearPayload_NullsPayload()
    {
        var node = new ChainNode<WorldImage>();
        node.InitializeBase(5);
        node.Payload = new WorldImage();

        node.ClearPayload();

        Assert.Null(node.Payload);
    }

    // ── Chain iterator ──────────────────────────────────────────────

    [Fact]
    public void Chain_SingleNode_YieldsOneElement()
    {
        var node = new ChainNode<WorldImage>();
        node.InitializeBase(0);
        node.Payload = new WorldImage();

        var ticks = new List<int>();
        foreach (var n in ChainNode<WorldImage>.Chain(null, node))
            ticks.Add(n.SequenceNumber);

        Assert.Equal([0], ticks);
    }

    [Fact]
    public void Chain_MultipleNodes_YieldsAllInOrder()
    {
        var a = new ChainNode<WorldImage>();
        var b = new ChainNode<WorldImage>();
        var c = new ChainNode<WorldImage>();

        a.InitializeBase(1);
        a.Payload = new WorldImage();
        b.InitializeBase(2);
        b.Payload = new WorldImage();
        c.InitializeBase(3);
        c.Payload = new WorldImage();
        a.SetNext(b);
        b.SetNext(c);

        var ticks = new List<int>();
        foreach (var n in ChainNode<WorldImage>.Chain(a, c))
            ticks.Add(n.SequenceNumber);

        Assert.Equal([1, 2, 3], ticks);
    }

    [Fact]
    public void Chain_StopsAtEnd_DoesNotReadBeyond()
    {
        var a = new ChainNode<WorldImage>();
        var b = new ChainNode<WorldImage>();
        var c = new ChainNode<WorldImage>();

        a.InitializeBase(1);
        a.Payload = new WorldImage();
        b.InitializeBase(2);
        b.Payload = new WorldImage();
        c.InitializeBase(3);
        c.Payload = new WorldImage();
        a.SetNext(b);
        b.SetNext(c);

        var ticks = new List<int>();
        foreach (var n in ChainNode<WorldImage>.Chain(a, b))
            ticks.Add(n.SequenceNumber);

        Assert.Equal([1, 2], ticks);
    }

    [Fact]
    public void Chain_NullStart_YieldsOnlyEnd()
    {
        var node = new ChainNode<WorldImage>();
        node.InitializeBase(5);
        node.Payload = new WorldImage();

        var ticks = new List<int>();
        foreach (var n in ChainNode<WorldImage>.Chain(null, node))
            ticks.Add(n.SequenceNumber);

        Assert.Equal([5], ticks);
    }
}
