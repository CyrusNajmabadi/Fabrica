using System.Runtime.InteropServices;
using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class ThreadLocalBufferTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TestNode
    {
        public int Value;
        public Handle<TestNode> Child;
    }

    // ═══════════════════════════ Basic allocation ═════════════════════════

    [Fact]
    public void Empty_CountIsZero()
        => Assert.Equal(0, new ThreadLocalBuffer<TestNode>(threadId: 0).Count);

    [Fact]
    public void Allocate_IncrementsCount()
    {
        var threadLocalBuffer = new ThreadLocalBuffer<TestNode>(threadId: 0);
        threadLocalBuffer.Allocate(default);
        Assert.Equal(1, threadLocalBuffer.Count);
        threadLocalBuffer.Allocate(default);
        Assert.Equal(2, threadLocalBuffer.Count);
    }

    // ═══════════════════════════ Handle encoding ═════════════════════════

    [Fact]
    public void Allocate_ReturnsLocalHandle()
    {
        var threadLocalBuffer = new ThreadLocalBuffer<TestNode>(threadId: 5);
        var handle = threadLocalBuffer.Allocate(default);

        Assert.True(TaggedHandle.IsLocal(handle.Index));
        Assert.Equal(5, TaggedHandle.DecodeThreadId(handle.Index));
        Assert.Equal(0, TaggedHandle.DecodeLocalIndex(handle.Index));
    }

    [Fact]
    public void Allocate_SequentialLocalIndices()
    {
        var threadLocalBuffer = new ThreadLocalBuffer<TestNode>(threadId: 3);
        var firstHandle = threadLocalBuffer.Allocate(default);
        var secondHandle = threadLocalBuffer.Allocate(default);
        var thirdHandle = threadLocalBuffer.Allocate(default);

        Assert.Equal(0, TaggedHandle.DecodeLocalIndex(firstHandle.Index));
        Assert.Equal(1, TaggedHandle.DecodeLocalIndex(secondHandle.Index));
        Assert.Equal(2, TaggedHandle.DecodeLocalIndex(thirdHandle.Index));

        Assert.Equal(3, TaggedHandle.DecodeThreadId(firstHandle.Index));
        Assert.Equal(3, TaggedHandle.DecodeThreadId(secondHandle.Index));
        Assert.Equal(3, TaggedHandle.DecodeThreadId(thirdHandle.Index));
    }

    // ═══════════════════════════ Indexer ══════════════════════════════════

    [Fact]
    public void Indexer_WriteAndRead()
    {
        var threadLocalBuffer = new ThreadLocalBuffer<TestNode>(threadId: 0);
        var handle = threadLocalBuffer.Allocate(new TestNode { Value = 42, Child = Handle<TestNode>.None });

        Assert.Equal(42, threadLocalBuffer[handle].Value);
        Assert.Equal(Handle<TestNode>.None, threadLocalBuffer[handle].Child);
    }

    [Fact]
    public void Indexer_MutateByRef()
    {
        var threadLocalBuffer = new ThreadLocalBuffer<TestNode>(threadId: 0);
        var handle = threadLocalBuffer.Allocate(default);

        ref var node = ref threadLocalBuffer[handle];
        node.Value = 99;

        Assert.Equal(99, threadLocalBuffer[handle].Value);
    }

    // ═══════════════════════════ WrittenSpan ══════════════════════════════

    [Fact]
    public void WrittenSpan_ReflectsAllocations()
    {
        var threadLocalBuffer = new ThreadLocalBuffer<TestNode>(threadId: 0);
        threadLocalBuffer.Allocate(new TestNode { Value = 10 });
        threadLocalBuffer.Allocate(new TestNode { Value = 20 });

        var span = threadLocalBuffer.WrittenSpan;
        Assert.Equal(2, span.Length);
        Assert.Equal(10, span[0].Value);
        Assert.Equal(20, span[1].Value);
    }

    [Fact]
    public void WrittenSpan_Empty()
    {
        var threadLocalBuffer = new ThreadLocalBuffer<TestNode>(threadId: 0);
        Assert.True(threadLocalBuffer.WrittenSpan.IsEmpty);
    }

    // ═══════════════════════════ Reset ════════════════════════════════════

    [Fact]
    public void Reset_ClearsCount()
    {
        var threadLocalBuffer = new ThreadLocalBuffer<TestNode>(threadId: 0);
        threadLocalBuffer.Allocate(default);
        threadLocalBuffer.Allocate(default);
        threadLocalBuffer.Allocate(default);

        threadLocalBuffer.Reset();

        Assert.Equal(0, threadLocalBuffer.Count);
        Assert.True(threadLocalBuffer.WrittenSpan.IsEmpty);
    }

    [Fact]
    public void Reset_AllowsReuse()
    {
        var threadLocalBuffer = new ThreadLocalBuffer<TestNode>(threadId: 7);
        threadLocalBuffer.Allocate(default);
        threadLocalBuffer.Reset();

        var handle = threadLocalBuffer.Allocate(default);
        Assert.Equal(1, threadLocalBuffer.Count);
        Assert.Equal(0, TaggedHandle.DecodeLocalIndex(handle.Index));
        Assert.Equal(7, TaggedHandle.DecodeThreadId(handle.Index));
    }

    // ═══════════════════════════ Growth ═══════════════════════════════════

    [Fact]
    public void GrowsBeyondInitialCapacity()
    {
        var threadLocalBuffer = new ThreadLocalBuffer<TestNode>(threadId: 0, initialCapacity: 4);

        var handles = new Handle<TestNode>[100];
        for (var i = 0; i < 100; i++)
        {
            handles[i] = threadLocalBuffer.Allocate(new TestNode { Value = i });
        }

        Assert.Equal(100, threadLocalBuffer.Count);

        for (var i = 0; i < 100; i++)
            Assert.Equal(i, threadLocalBuffer[handles[i]].Value);
    }

    // ═══════════════════════════ Cross-thread references ══════════════════

    [Fact]
    public void CrossThreadReference_EncodesCorrectly()
    {
        var threadLocalBufferA = new ThreadLocalBuffer<TestNode>(threadId: 0);
        var threadLocalBufferB = new ThreadLocalBuffer<TestNode>(threadId: 1);

        var handleB = threadLocalBufferB.Allocate(new TestNode { Value = 42 });

        var handleA = threadLocalBufferA.Allocate(new TestNode { Value = 1, Child = handleB });

        Assert.Equal(1, TaggedHandle.DecodeThreadId(threadLocalBufferA[handleA].Child.Index));
        Assert.Equal(0, TaggedHandle.DecodeLocalIndex(threadLocalBufferA[handleA].Child.Index));
    }

    // ═══════════════════════════ Root tracking ═════════════════════════════

    [Fact]
    public void RootHandles_EmptyByDefault()
    {
        var threadLocalBuffer = new ThreadLocalBuffer<TestNode>(threadId: 0);
        threadLocalBuffer.Allocate(default);
        Assert.True(threadLocalBuffer.RootHandles.IsEmpty);
    }

    [Fact]
    public void MarkRoot_AppearsInRootHandles()
    {
        var threadLocalBuffer = new ThreadLocalBuffer<TestNode>(threadId: 0);
        var firstHandle = threadLocalBuffer.Allocate(default);
        var secondHandle = threadLocalBuffer.Allocate(default);

        threadLocalBuffer.MarkRoot(firstHandle);

        Assert.Equal(1, threadLocalBuffer.RootHandles.Length);
        Assert.Equal(firstHandle, threadLocalBuffer.RootHandles[0]);
    }

    [Fact]
    public void MarkRoot_MultipleRoots()
    {
        var threadLocalBuffer = new ThreadLocalBuffer<TestNode>(threadId: 0);
        var firstHandle = threadLocalBuffer.Allocate(default);
        var secondHandle = threadLocalBuffer.Allocate(default);
        var thirdHandle = threadLocalBuffer.Allocate(default);

        threadLocalBuffer.MarkRoot(firstHandle);
        threadLocalBuffer.MarkRoot(thirdHandle);

        Assert.Equal(2, threadLocalBuffer.RootHandles.Length);
        Assert.Equal(firstHandle, threadLocalBuffer.RootHandles[0]);
        Assert.Equal(thirdHandle, threadLocalBuffer.RootHandles[1]);
    }

    [Fact]
    public void Allocate_IsRoot_RecordsRoot()
    {
        var threadLocalBuffer = new ThreadLocalBuffer<TestNode>(threadId: 0);
        var firstHandle = threadLocalBuffer.Allocate(default, isRoot: true);
        var secondHandle = threadLocalBuffer.Allocate(default, isRoot: false);
        var thirdHandle = threadLocalBuffer.Allocate(default, isRoot: true);

        Assert.Equal(3, threadLocalBuffer.Count);
        Assert.Equal(2, threadLocalBuffer.RootHandles.Length);
        Assert.Equal(firstHandle, threadLocalBuffer.RootHandles[0]);
        Assert.Equal(thirdHandle, threadLocalBuffer.RootHandles[1]);
    }

    [Fact]
    public void Allocate_DefaultOverload_IsNotRoot()
    {
        var threadLocalBuffer = new ThreadLocalBuffer<TestNode>(threadId: 0);
        threadLocalBuffer.Allocate(default);
        threadLocalBuffer.Allocate(default);
        Assert.True(threadLocalBuffer.RootHandles.IsEmpty);
    }

    [Fact]
    public void Reset_ClearsRoots()
    {
        var threadLocalBuffer = new ThreadLocalBuffer<TestNode>(threadId: 0);
        threadLocalBuffer.Allocate(default, isRoot: true);
        threadLocalBuffer.Allocate(default, isRoot: true);
        Assert.Equal(2, threadLocalBuffer.RootHandles.Length);

        threadLocalBuffer.Reset();

        Assert.True(threadLocalBuffer.RootHandles.IsEmpty);
    }

    [Fact]
    public void Reset_RootsReuseAfterReset()
    {
        var threadLocalBuffer = new ThreadLocalBuffer<TestNode>(threadId: 0);
        threadLocalBuffer.Allocate(default, isRoot: true);
        threadLocalBuffer.Reset();

        var rootHandle = threadLocalBuffer.Allocate(default, isRoot: true);
        Assert.Equal(1, threadLocalBuffer.RootHandles.Length);
        Assert.Equal(rootHandle, threadLocalBuffer.RootHandles[0]);
    }

    [Fact]
    public void MarkRoot_CrossTlbHandle()
    {
        var threadLocalBufferA = new ThreadLocalBuffer<TestNode>(threadId: 0);
        var threadLocalBufferB = new ThreadLocalBuffer<TestNode>(threadId: 1);

        var handleB = threadLocalBufferB.Allocate(default);

        threadLocalBufferA.MarkRoot(handleB);

        Assert.Equal(1, threadLocalBufferA.RootHandles.Length);
        Assert.Equal(handleB, threadLocalBufferA.RootHandles[0]);
        Assert.Equal(1, TaggedHandle.DecodeThreadId(threadLocalBufferA.RootHandles[0].Index));
    }
}
