using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class ThreadLocalBufferTests
{
    // ═══════════════════════════ Helpers ═════════════════════════════════

    private struct SimpleNode : IArenaNode
    {
        public int Left { get; set; }
        public int Right { get; set; }
        public int Value { get; set; }

        public void FixupReferences(ReadOnlySpan<int> localToGlobalMap)
        {
            if (ArenaIndex.IsLocal(this.Left))
                this.Left = localToGlobalMap[ArenaIndex.UntagLocal(this.Left)];
            if (ArenaIndex.IsLocal(this.Right))
                this.Right = localToGlobalMap[ArenaIndex.UntagLocal(this.Right)];
        }

        public readonly void IncrementChildren(RefCountTable table)
        {
            if (this.Left != ArenaIndex.NoChild)
                table.Increment(this.Left);
            if (this.Right != ArenaIndex.NoChild)
                table.Increment(this.Right);
        }
    }

    // ═══════════════════════════ Basic operations ═════════════════════════

    [Fact]
    public void New_CountIsZero()
    {
        var buffer = new ThreadLocalBuffer<SimpleNode>();
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void Append_ReturnsSequentialIndices()
    {
        var buffer = new ThreadLocalBuffer<SimpleNode>();

        Assert.Equal(0, buffer.Append(new SimpleNode { Value = 10 }));
        Assert.Equal(1, buffer.Append(new SimpleNode { Value = 20 }));
        Assert.Equal(2, buffer.Append(new SimpleNode { Value = 30 }));
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void Indexer_ReturnsAppendedNode()
    {
        var buffer = new ThreadLocalBuffer<SimpleNode>();
        buffer.Append(new SimpleNode { Value = 42, Left = ArenaIndex.NoChild, Right = ArenaIndex.NoChild });

        Assert.Equal(42, buffer[0].Value);
        Assert.Equal(ArenaIndex.NoChild, buffer[0].Left);
    }

    [Fact]
    public void Indexer_ReturnsRef_AllowsInPlaceModification()
    {
        var buffer = new ThreadLocalBuffer<SimpleNode>();
        buffer.Append(new SimpleNode { Value = 1 });

        buffer[0].Value = 99;
        Assert.Equal(99, buffer[0].Value);
    }

    [Fact]
    public void Nodes_ReturnsSpanOfAppended()
    {
        var buffer = new ThreadLocalBuffer<SimpleNode>();
        buffer.Append(new SimpleNode { Value = 1 });
        buffer.Append(new SimpleNode { Value = 2 });
        buffer.Append(new SimpleNode { Value = 3 });

        var nodes = buffer.Nodes;
        Assert.Equal(3, nodes.Length);
        Assert.Equal(1, nodes[0].Value);
        Assert.Equal(2, nodes[1].Value);
        Assert.Equal(3, nodes[2].Value);
    }

    // ═══════════════════════════ Growth ═══════════════════════════════════

    [Fact]
    public void Append_GrowsBeyondInitialCapacity()
    {
        var buffer = new ThreadLocalBuffer<SimpleNode>(initialCapacity: 4);
        var ta = buffer.GetTestAccessor();

        Assert.Equal(4, ta.Capacity);

        for (var i = 0; i < 100; i++)
            buffer.Append(new SimpleNode { Value = i });

        Assert.Equal(100, buffer.Count);
        Assert.True(ta.Capacity >= 100);

        for (var i = 0; i < 100; i++)
            Assert.Equal(i, buffer[i].Value);
    }

    // ═══════════════════════════ Release log ═════════════════════════════

    [Fact]
    public void LogRelease_ThenPopAll()
    {
        var buffer = new ThreadLocalBuffer<SimpleNode>();
        buffer.LogRelease(10);
        buffer.LogRelease(20);
        buffer.LogRelease(30);

        var ta = buffer.GetTestAccessor();
        Assert.Equal(3, ta.ReleaseLogCount);

        var released = new List<int>();
        while (buffer.TryPopRelease(out var index))
            released.Add(index);

        Assert.Equal(3, released.Count);
        Assert.Contains(10, released);
        Assert.Contains(20, released);
        Assert.Contains(30, released);
    }

    [Fact]
    public void TryPopRelease_Empty_ReturnsFalse()
    {
        var buffer = new ThreadLocalBuffer<SimpleNode>();
        Assert.False(buffer.TryPopRelease(out _));
    }

    // ═══════════════════════════ Clear and reuse ═════════════════════════

    [Fact]
    public void Clear_ResetsCountToZero()
    {
        var buffer = new ThreadLocalBuffer<SimpleNode>();
        buffer.Append(new SimpleNode { Value = 1 });
        buffer.Append(new SimpleNode { Value = 2 });
        buffer.LogRelease(99);

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.False(buffer.TryPopRelease(out _));
    }

    [Fact]
    public void Clear_RetainsBackingCapacity()
    {
        var buffer = new ThreadLocalBuffer<SimpleNode>(initialCapacity: 4);
        var ta = buffer.GetTestAccessor();

        for (var i = 0; i < 100; i++)
            buffer.Append(new SimpleNode { Value = i });

        var capacityBefore = ta.Capacity;

        buffer.Clear();

        Assert.True(ta.Capacity >= capacityBefore);
    }

    [Fact]
    public void Clear_ThenReuseBuffer()
    {
        var buffer = new ThreadLocalBuffer<SimpleNode>();
        buffer.Append(new SimpleNode { Value = 1 });
        buffer.LogRelease(50);

        buffer.Clear();

        Assert.Equal(0, buffer.Append(new SimpleNode { Value = 99 }));
        Assert.Equal(1, buffer.Count);
        Assert.Equal(99, buffer[0].Value);
    }

    // ═══════════════════════════ Tagged local references ═════════════════

    [Fact]
    public void TaggedLocalReferences_InChildFields()
    {
        var buffer = new ThreadLocalBuffer<SimpleNode>();

        var leafIdx = buffer.Append(new SimpleNode
        {
            Value = 1,
            Left = ArenaIndex.NoChild,
            Right = ArenaIndex.NoChild,
        });

        var rootIdx = buffer.Append(new SimpleNode
        {
            Value = 2,
            Left = ArenaIndex.TagLocal(leafIdx),
            Right = ArenaIndex.NoChild,
        });

        Assert.True(ArenaIndex.IsLocal(buffer[rootIdx].Left));
        Assert.Equal(leafIdx, ArenaIndex.UntagLocal(buffer[rootIdx].Left));
    }
}
