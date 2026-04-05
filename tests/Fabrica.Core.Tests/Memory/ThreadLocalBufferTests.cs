using System.Runtime.InteropServices;
using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class ThreadLocalBufferTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TestNode
    {
        public int _value;
        public Handle<TestNode> _child;
    }

    // ═══════════════════════════ Basic allocation ═════════════════════════

    [Fact]
    public void Empty_CountIsZero()
        => Assert.Equal(0, new ThreadLocalBuffer<TestNode>(threadId: 0).Count);

    [Fact]
    public void Allocate_IncrementsCount()
    {
        var tlb = new ThreadLocalBuffer<TestNode>(threadId: 0);
        tlb.Allocate();
        Assert.Equal(1, tlb.Count);
        tlb.Allocate();
        Assert.Equal(2, tlb.Count);
    }

    // ═══════════════════════════ Handle encoding ═════════════════════════

    [Fact]
    public void Allocate_ReturnsLocalHandle()
    {
        var tlb = new ThreadLocalBuffer<TestNode>(threadId: 5);
        var handle = tlb.Allocate();

        Assert.True(TaggedHandle.IsLocal(handle.Index));
        Assert.Equal(5, TaggedHandle.DecodeThreadId(handle.Index));
        Assert.Equal(0, TaggedHandle.DecodeLocalIndex(handle.Index));
    }

    [Fact]
    public void Allocate_SequentialLocalIndices()
    {
        var tlb = new ThreadLocalBuffer<TestNode>(threadId: 3);
        var h0 = tlb.Allocate();
        var h1 = tlb.Allocate();
        var h2 = tlb.Allocate();

        Assert.Equal(0, TaggedHandle.DecodeLocalIndex(h0.Index));
        Assert.Equal(1, TaggedHandle.DecodeLocalIndex(h1.Index));
        Assert.Equal(2, TaggedHandle.DecodeLocalIndex(h2.Index));

        Assert.Equal(3, TaggedHandle.DecodeThreadId(h0.Index));
        Assert.Equal(3, TaggedHandle.DecodeThreadId(h1.Index));
        Assert.Equal(3, TaggedHandle.DecodeThreadId(h2.Index));
    }

    // ═══════════════════════════ Indexer ══════════════════════════════════

    [Fact]
    public void Indexer_WriteAndRead()
    {
        var tlb = new ThreadLocalBuffer<TestNode>(threadId: 0);
        var handle = tlb.Allocate();
        var localIndex = TaggedHandle.DecodeLocalIndex(handle.Index);

        tlb[localIndex] = new TestNode { _value = 42, _child = Handle<TestNode>.None };

        Assert.Equal(42, tlb[localIndex]._value);
        Assert.Equal(Handle<TestNode>.None, tlb[localIndex]._child);
    }

    [Fact]
    public void Indexer_MutateByRef()
    {
        var tlb = new ThreadLocalBuffer<TestNode>(threadId: 0);
        tlb.Allocate();

        ref var node = ref tlb[0];
        node._value = 99;

        Assert.Equal(99, tlb[0]._value);
    }

    // ═══════════════════════════ WrittenSpan ══════════════════════════════

    [Fact]
    public void WrittenSpan_ReflectsAllocations()
    {
        var tlb = new ThreadLocalBuffer<TestNode>(threadId: 0);
        tlb.Allocate();
        tlb[0] = new TestNode { _value = 10 };
        tlb.Allocate();
        tlb[1] = new TestNode { _value = 20 };

        var span = tlb.WrittenSpan;
        Assert.Equal(2, span.Length);
        Assert.Equal(10, span[0]._value);
        Assert.Equal(20, span[1]._value);
    }

    [Fact]
    public void WrittenSpan_Empty()
    {
        var tlb = new ThreadLocalBuffer<TestNode>(threadId: 0);
        Assert.True(tlb.WrittenSpan.IsEmpty);
    }

    // ═══════════════════════════ Reset ════════════════════════════════════

    [Fact]
    public void Reset_ClearsCount()
    {
        var tlb = new ThreadLocalBuffer<TestNode>(threadId: 0);
        tlb.Allocate();
        tlb.Allocate();
        tlb.Allocate();

        tlb.Reset();

        Assert.Equal(0, tlb.Count);
        Assert.True(tlb.WrittenSpan.IsEmpty);
    }

    [Fact]
    public void Reset_AllowsReuse()
    {
        var tlb = new ThreadLocalBuffer<TestNode>(threadId: 7);
        tlb.Allocate();
        tlb.Reset();

        var handle = tlb.Allocate();
        Assert.Equal(1, tlb.Count);
        Assert.Equal(0, TaggedHandle.DecodeLocalIndex(handle.Index));
        Assert.Equal(7, TaggedHandle.DecodeThreadId(handle.Index));
    }

    // ═══════════════════════════ Growth ═══════════════════════════════════

    [Fact]
    public void GrowsBeyondInitialCapacity()
    {
        var tlb = new ThreadLocalBuffer<TestNode>(threadId: 0, initialCapacity: 4);

        for (var i = 0; i < 100; i++)
        {
            var handle = tlb.Allocate();
            var localIndex = TaggedHandle.DecodeLocalIndex(handle.Index);
            tlb[localIndex] = new TestNode { _value = i };
        }

        Assert.Equal(100, tlb.Count);

        for (var i = 0; i < 100; i++)
            Assert.Equal(i, tlb[i]._value);
    }

    // ═══════════════════════════ Cross-thread references ══════════════════

    [Fact]
    public void CrossThreadReference_EncodesCorrectly()
    {
        var tlbA = new ThreadLocalBuffer<TestNode>(threadId: 0);
        var tlbB = new ThreadLocalBuffer<TestNode>(threadId: 1);

        var handleB = tlbB.Allocate();
        tlbB[0] = new TestNode { _value = 42 };

        var handleA = tlbA.Allocate();
        tlbA[0] = new TestNode { _value = 1, _child = handleB };

        Assert.Equal(1, TaggedHandle.DecodeThreadId(tlbA[0]._child.Index));
        Assert.Equal(0, TaggedHandle.DecodeLocalIndex(tlbA[0]._child.Index));
    }
}
