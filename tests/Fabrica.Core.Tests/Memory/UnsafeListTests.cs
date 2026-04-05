using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class UnsafeListTests
{
    // ═══════════════════════════ Basic operations ═════════════════════════

    [Fact]
    public void Empty_CountIsZero()
        => Assert.Equal(0, new UnsafeList<int>().Count);

    [Fact]
    public void Add_IncrementsCount()
    {
        var list = new UnsafeList<int>();
        list.Add(1);
        Assert.Equal(1, list.Count);
        list.Add(2);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void Indexer_ReturnsAddedItems()
    {
        var list = new UnsafeList<int>();
        list.Add(10);
        list.Add(20);
        list.Add(30);

        Assert.Equal(10, list[0]);
        Assert.Equal(20, list[1]);
        Assert.Equal(30, list[2]);
    }

    [Fact]
    public void Indexer_ReturnsByRef()
    {
        var list = new UnsafeList<int>();
        list.Add(42);

        ref var item = ref list[0];
        item = 99;

        Assert.Equal(99, list[0]);
    }

    // ═══════════════════════════ WrittenSpan ══════════════════════════════

    [Fact]
    public void WrittenSpan_ReflectsAdds()
    {
        var list = new UnsafeList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3);

        var span = list.WrittenSpan;
        Assert.Equal(3, span.Length);
        Assert.Equal(1, span[0]);
        Assert.Equal(2, span[1]);
        Assert.Equal(3, span[2]);
    }

    [Fact]
    public void WrittenSpanMutable_AllowsInPlaceMutation()
    {
        var list = new UnsafeList<int>();
        list.Add(10);
        list.Add(20);

        var span = list.WrittenSpanMutable;
        span[0] = 100;
        span[1] = 200;

        Assert.Equal(100, list[0]);
        Assert.Equal(200, list[1]);
    }

    [Fact]
    public void WrittenSpan_EmptyList_ReturnsEmpty()
    {
        var list = new UnsafeList<int>();
        Assert.True(list.WrittenSpan.IsEmpty);
    }

    // ═══════════════════════════ Reset ════════════════════════════════════

    [Fact]
    public void Reset_ClearsCount()
    {
        var list = new UnsafeList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3);

        list.Reset();

        Assert.Equal(0, list.Count);
        Assert.True(list.WrittenSpan.IsEmpty);
    }

    [Fact]
    public void Reset_AllowsReuse()
    {
        var list = new UnsafeList<int>();
        list.Add(1);
        list.Add(2);
        list.Reset();

        list.Add(10);
        list.Add(20);
        list.Add(30);

        Assert.Equal(3, list.Count);
        Assert.Equal(10, list[0]);
        Assert.Equal(20, list[1]);
        Assert.Equal(30, list[2]);
    }

    // ═══════════════════════════ RemoveLast ═══════════════════════════════

    [Fact]
    public void RemoveLast_DecrementsCount()
    {
        var list = new UnsafeList<int>();
        list.Add(1);
        list.Add(2);

        list.RemoveLast();

        Assert.Equal(1, list.Count);
        Assert.Equal(1, list[0]);
    }

    [Fact]
    public void RemoveLast_ThenAdd_Overwrites()
    {
        var list = new UnsafeList<int>();
        list.Add(10);
        list.Add(20);
        list.RemoveLast();
        list.Add(30);

        Assert.Equal(2, list.Count);
        Assert.Equal(10, list[0]);
        Assert.Equal(30, list[1]);
    }

    // ═══════════════════════════ Growth ═══════════════════════════════════

    [Fact]
    public void GrowsBeyondInitialCapacity()
    {
        var list = new UnsafeList<int>(initialCapacity: 4);

        for (var i = 0; i < 100; i++)
            list.Add(i);

        Assert.Equal(100, list.Count);

        for (var i = 0; i < 100; i++)
            Assert.Equal(i, list[i]);
    }

    [Fact]
    public void GrowPreservesExistingItems()
    {
        var list = new UnsafeList<int>(initialCapacity: 2);
        list.Add(1);
        list.Add(2);
        list.Add(3);

        Assert.Equal(1, list[0]);
        Assert.Equal(2, list[1]);
        Assert.Equal(3, list[2]);
    }

    [Fact]
    public void MultipleGrows()
    {
        var list = new UnsafeList<int>(initialCapacity: 1);

        for (var i = 0; i < 1024; i++)
            list.Add(i);

        Assert.Equal(1024, list.Count);

        for (var i = 0; i < 1024; i++)
            Assert.Equal(i, list[i]);
    }

    // ═══════════════════════════ Reference types ══════════════════════════

    [Fact]
    public void WorksWithReferenceTypes()
    {
        var list = new UnsafeList<string>();
        list.Add("hello");
        list.Add("world");

        Assert.Equal("hello", list[0]);
        Assert.Equal("world", list[1]);
        Assert.Equal(2, list.Count);
    }

    // ═══════════════════════════ Struct by-ref mutation ═══════════════════

    [Fact]
    public void StructByRef_MutatesInPlace()
    {
        var list = new UnsafeList<Point>();
        list.Add(new Point { _x = 1, _y = 2 });

        ref var p = ref list[0];
        p._x = 10;
        p._y = 20;

        Assert.Equal(10, list[0]._x);
        Assert.Equal(20, list[0]._y);
    }

    private struct Point
    {
        public int _x;
        public int _y;
    }
}
