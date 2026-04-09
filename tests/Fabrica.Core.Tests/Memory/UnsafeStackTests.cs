using Fabrica.Core.Collections.Unsafe;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class UnsafeStackTests
{
    // ═══════════════════════════ Basic operations ═════════════════════════

    [Fact]
    public void Empty_CountIsZero()
        => Assert.Equal(0, UnsafeStack<int>.Create().Count);

    [Fact]
    public void Push_IncrementsCount()
    {
        var stack = UnsafeStack<int>.Create();
        stack.Push(1);
        Assert.Equal(1, stack.Count);
        stack.Push(2);
        Assert.Equal(2, stack.Count);
    }

    [Fact]
    public void TryPop_Empty_ReturnsFalse()
    {
        var stack = UnsafeStack<int>.Create();
        Assert.False(stack.TryPop(out _));
    }

    [Fact]
    public void TryPop_Empty_OutputsDefault()
    {
        var stack = UnsafeStack<int>.Create();
        stack.TryPop(out var item);
        Assert.Equal(0, item);
    }

    [Fact]
    public void PushThenPop_ReturnsItem()
    {
        var stack = UnsafeStack<int>.Create();
        stack.Push(42);
        Assert.True(stack.TryPop(out var item));
        Assert.Equal(42, item);
        Assert.Equal(0, stack.Count);
    }

    // ═══════════════════════════ LIFO ordering ════════════════════════════

    [Fact]
    public void IsLifo()
    {
        var stack = UnsafeStack<int>.Create();
        stack.Push(1);
        stack.Push(2);
        stack.Push(3);

        Assert.True(stack.TryPop(out var a));
        Assert.Equal(3, a);
        Assert.True(stack.TryPop(out var b));
        Assert.Equal(2, b);
        Assert.True(stack.TryPop(out var c));
        Assert.Equal(1, c);
        Assert.False(stack.TryPop(out _));
    }

    [Fact]
    public void InterleavedPushPop_MaintainsLifo()
    {
        var stack = UnsafeStack<int>.Create();
        stack.Push(10);
        stack.Push(20);

        Assert.True(stack.TryPop(out var a));
        Assert.Equal(20, a);

        stack.Push(30);
        stack.Push(40);

        Assert.True(stack.TryPop(out var b));
        Assert.Equal(40, b);
        Assert.True(stack.TryPop(out var c));
        Assert.Equal(30, c);
        Assert.True(stack.TryPop(out var d));
        Assert.Equal(10, d);

        Assert.Equal(0, stack.Count);
    }

    // ═══════════════════════════ Growth ═══════════════════════════════════

    [Fact]
    public void GrowsBeyondInitialCapacity()
    {
        var stack = new UnsafeStack<int>(initialCapacity: 4);

        for (var i = 0; i < 100; i++)
            stack.Push(i);

        Assert.Equal(100, stack.Count);

        for (var i = 99; i >= 0; i--)
        {
            Assert.True(stack.TryPop(out var item));
            Assert.Equal(i, item);
        }

        Assert.Equal(0, stack.Count);
    }

    [Fact]
    public void GrowPreservesExistingItems()
    {
        var stack = new UnsafeStack<int>(initialCapacity: 2);
        stack.Push(1);
        stack.Push(2);
        // This push triggers a grow
        stack.Push(3);

        Assert.True(stack.TryPop(out var a));
        Assert.Equal(3, a);
        Assert.True(stack.TryPop(out var b));
        Assert.Equal(2, b);
        Assert.True(stack.TryPop(out var c));
        Assert.Equal(1, c);
    }

    [Fact]
    public void MultipleGrows()
    {
        var stack = new UnsafeStack<int>(initialCapacity: 1);

        for (var i = 0; i < 1024; i++)
            stack.Push(i);

        Assert.Equal(1024, stack.Count);

        for (var i = 1023; i >= 0; i--)
        {
            Assert.True(stack.TryPop(out var item));
            Assert.Equal(i, item);
        }
    }

    // ═══════════════════════════ Push after drain ═════════════════════════

    [Fact]
    public void PushAfterFullDrain()
    {
        var stack = new UnsafeStack<int>(initialCapacity: 4);

        for (var i = 0; i < 10; i++)
            stack.Push(i);
        for (var i = 0; i < 10; i++)
            stack.TryPop(out _);

        Assert.Equal(0, stack.Count);

        stack.Push(99);
        Assert.Equal(1, stack.Count);
        Assert.True(stack.TryPop(out var item));
        Assert.Equal(99, item);
    }

    [Fact]
    public void RepeatedFillAndDrain()
    {
        var stack = new UnsafeStack<int>(initialCapacity: 4);

        for (var cycle = 0; cycle < 5; cycle++)
        {
            for (var i = 0; i < 20; i++)
                stack.Push(i + (cycle * 100));

            Assert.Equal(20, stack.Count);

            for (var i = 19; i >= 0; i--)
            {
                Assert.True(stack.TryPop(out var item));
                Assert.Equal(i + (cycle * 100), item);
            }

            Assert.Equal(0, stack.Count);
        }
    }

    // ═══════════════════════════ Reference types ══════════════════════════

    [Fact]
    public void WorksWithReferenceTypes()
    {
        var stack = UnsafeStack<string>.Create();
        stack.Push("hello");
        stack.Push("world");

        Assert.True(stack.TryPop(out var a));
        Assert.Equal("world", a);
        Assert.True(stack.TryPop(out var b));
        Assert.Equal("hello", b);
    }

    // ═══════════════════════════ Default initial capacity ═════════════════

    [Fact]
    public void DefaultCapacity_WorksCorrectly()
    {
        var stack = UnsafeStack<int>.Create();

        for (var i = 0; i < 100; i++)
            stack.Push(i);

        Assert.Equal(100, stack.Count);

        Assert.True(stack.TryPop(out var top));
        Assert.Equal(99, top);
    }
}
