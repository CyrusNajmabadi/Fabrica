using Simulation.World;
using Xunit;

namespace Simulation.Tests.World;

/// <summary>
/// Tests for <see cref="ChainNode{TSelf}"/> base class mechanics, exercised
/// through <see cref="WorldSnapshot"/> (the only concrete implementation).
/// </summary>
public sealed class ChainNodeTests
{
    [Fact]
    public void InitializeBase_SetsSequenceNumberAndClearsChainState()
    {
        var snapshot = new WorldSnapshot();
        snapshot.Initialize(new WorldImage(), tickNumber: 7);

        Assert.Equal(7, snapshot.SequenceNumber);
        Assert.Equal(0, snapshot.PublishTimeNanoseconds);
        Assert.Null(snapshot.NextInChain);
        Assert.False(snapshot.IsUnreferenced);
    }

    [Fact]
    public void SetNext_LinksNodes()
    {
        var first = new WorldSnapshot();
        var second = new WorldSnapshot();

        first.Initialize(new WorldImage(), 0);
        second.Initialize(new WorldImage(), 1);

        first.SetNext(second);

        Assert.Same(second, first.NextInChain);
    }

    [Fact]
    public void ClearNext_RemovesLink()
    {
        var first = new WorldSnapshot();
        var second = new WorldSnapshot();

        first.Initialize(new WorldImage(), 0);
        second.Initialize(new WorldImage(), 1);
        first.SetNext(second);

        first.ClearNext();

        Assert.Null(first.NextInChain);
    }

    [Fact]
    public void MarkPublished_SetsPublishTime()
    {
        var snapshot = new WorldSnapshot();
        snapshot.Initialize(new WorldImage(), 0);

        snapshot.MarkPublished(42_000_000);

        Assert.Equal(42_000_000, snapshot.PublishTimeNanoseconds);
    }

    [Fact]
    public void Initialize_ClearsPublishTime()
    {
        var snapshot = new WorldSnapshot();
        snapshot.Initialize(new WorldImage(), 0);
        snapshot.MarkPublished(999);
        snapshot.Release();

        snapshot.Initialize(new WorldImage(), 1);

        Assert.Equal(0, snapshot.PublishTimeNanoseconds);
    }

    [Fact]
    public void AddRef_PreventsReleaseFromReachingZero()
    {
        var snapshot = new WorldSnapshot();
        snapshot.Initialize(new WorldImage(), 5);

        snapshot.AddRef();
        snapshot.Release();

        Assert.False(snapshot.IsUnreferenced);
    }

    [Fact]
    public void AddRef_ThenFullRelease_ReachesZero()
    {
        var snapshot = new WorldSnapshot();
        snapshot.Initialize(new WorldImage(), 5);

        snapshot.AddRef();
        snapshot.Release();
        snapshot.Release();

        Assert.True(snapshot.IsUnreferenced);
    }

    [Fact]
    public void Release_ToZero_ClearsNextPointer()
    {
        var snapshot = new WorldSnapshot();
        var next = new WorldSnapshot();

        snapshot.Initialize(new WorldImage(), 10);
        next.Initialize(new WorldImage(), 11);
        snapshot.SetNext(next);

        snapshot.Release();

        Assert.True(snapshot.IsUnreferenced);
        Assert.Null(snapshot.NextInChain);
    }

    // ── Chain iterator ──────────────────────────────────────────────────

    [Fact]
    public void Chain_SingleNode_YieldsOneElement()
    {
        var node = new WorldSnapshot();
        node.Initialize(new WorldImage(), 0);

        var ticks = new List<int>();
        foreach (var n in ChainNode<WorldSnapshot>.Chain(null, node))
            ticks.Add(n.SequenceNumber);

        Assert.Equal([0], ticks);
    }

    [Fact]
    public void Chain_MultipleNodes_YieldsAllInOrder()
    {
        var a = new WorldSnapshot();
        var b = new WorldSnapshot();
        var c = new WorldSnapshot();

        a.Initialize(new WorldImage(), 1);
        b.Initialize(new WorldImage(), 2);
        c.Initialize(new WorldImage(), 3);
        a.SetNext(b);
        b.SetNext(c);

        var ticks = new List<int>();
        foreach (var n in ChainNode<WorldSnapshot>.Chain(a, c))
            ticks.Add(n.SequenceNumber);

        Assert.Equal([1, 2, 3], ticks);
    }

    [Fact]
    public void Chain_StopsAtEnd_DoesNotReadBeyond()
    {
        var a = new WorldSnapshot();
        var b = new WorldSnapshot();
        var c = new WorldSnapshot();

        a.Initialize(new WorldImage(), 1);
        b.Initialize(new WorldImage(), 2);
        c.Initialize(new WorldImage(), 3);
        a.SetNext(b);
        b.SetNext(c);

        var ticks = new List<int>();
        foreach (var n in ChainNode<WorldSnapshot>.Chain(a, b))
            ticks.Add(n.SequenceNumber);

        Assert.Equal([1, 2], ticks);
    }

    [Fact]
    public void Chain_NullStart_YieldsOnlyEnd()
    {
        var node = new WorldSnapshot();
        node.Initialize(new WorldImage(), 5);

        var ticks = new List<int>();
        foreach (var n in ChainNode<WorldSnapshot>.Chain(null, node))
            ticks.Add(n.SequenceNumber);

        Assert.Equal([5], ticks);
    }
}
