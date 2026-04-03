using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class UnsafeSlabArenaTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    private struct Int32Entry
    {
        public int Value { get; set; }
    }

    [StructLayout(LayoutKind.Sequential, Size = 4096)]
    private struct LargeEntry;

    [StructLayout(LayoutKind.Sequential, Size = 90_000)]
    private struct OversizedEntry;

    /// <summary>Creates an arena with tiny parameters for edge-case testing. With slabShift=2, each slab holds 4 entries.</summary>
    private static UnsafeSlabArena<Int32Entry> CreateTinyArena(int directoryLength = 4, int slabShift = 2)
        => new(directoryLength, slabShift);

    // ═══════════════════════════ Allocation basics ════════════════════════

    [Fact]
    public void Allocate_ReturnsZeroBasedContiguousIndices()
    {
        var arena = CreateTinyArena();
        Assert.Equal(0, arena.Allocate());
        Assert.Equal(1, arena.Allocate());
        Assert.Equal(2, arena.Allocate());
    }

    [Fact]
    public void Allocate_InitialState_IsEmpty()
    {
        var arena = CreateTinyArena();
        var ta = arena.GetTestAccessor();
        Assert.Equal(0, ta.Count);
        Assert.Equal(0, ta.HighWater);
        Assert.Equal(0, ta.FreeCount);
    }

    [Fact]
    public void Allocate_IncrementsCountAndHighWater()
    {
        var arena = CreateTinyArena();
        var ta = arena.GetTestAccessor();

        arena.Allocate();
        Assert.Equal(1, ta.Count);
        Assert.Equal(1, ta.HighWater);

        arena.Allocate();
        Assert.Equal(2, ta.Count);
        Assert.Equal(2, ta.HighWater);
    }

    [Fact]
    public void Indexer_ReadWrite_RoundTrips()
    {
        var arena = CreateTinyArena();
        var i0 = arena.Allocate();
        var i1 = arena.Allocate();

        arena[i0] = new Int32Entry { Value = 42 };
        arena[i1] = new Int32Entry { Value = 99 };

        Assert.Equal(42, arena[i0].Value);
        Assert.Equal(99, arena[i1].Value);
    }

    [Fact]
    public void Indexer_ReturnsRef_AllowsInPlaceMutation()
    {
        var arena = CreateTinyArena();
        var idx = arena.Allocate();
        arena[idx] = new Int32Entry { Value = 1 };

        ref var entry = ref arena[idx];
        entry.Value = 2;

        Assert.Equal(2, arena[idx].Value);
    }

    // ═══════════════════════════ Free list (LIFO) ════════════════════════

    [Fact]
    public void Free_DecrementsCount_ButNotHighWater()
    {
        var arena = CreateTinyArena();
        var ta = arena.GetTestAccessor();

        var i0 = arena.Allocate();
        arena.Allocate();
        arena.Free(i0);

        Assert.Equal(1, ta.Count);
        Assert.Equal(2, ta.HighWater);
        Assert.Equal(1, ta.FreeCount);
    }

    [Fact]
    public void Free_ThenAllocate_ReusesFreedIndex()
    {
        var arena = CreateTinyArena();
        var i0 = arena.Allocate();
        var i1 = arena.Allocate();
        arena.Free(i1);
        arena.Free(i0);

        var reused0 = arena.Allocate();
        var reused1 = arena.Allocate();

        // LIFO: i0 was freed last (pushed last), so it's popped first.
        Assert.Equal(i0, reused0);
        Assert.Equal(i1, reused1);
    }

    [Fact]
    public void Free_ThenAllocate_DoesNotAdvanceHighWater()
    {
        var arena = CreateTinyArena();
        var ta = arena.GetTestAccessor();

        var idx = arena.Allocate();
        arena.Free(idx);
        arena.Allocate();

        Assert.Equal(1, ta.HighWater);
        Assert.Equal(1, ta.Count);
        Assert.Equal(0, ta.FreeCount);
    }

    [Fact]
    public void FreeList_IsLifo()
    {
        var arena = CreateTinyArena();
        var i0 = arena.Allocate();
        var i1 = arena.Allocate();
        var i2 = arena.Allocate();

        arena.Free(i0);
        arena.Free(i1);
        arena.Free(i2);

        Assert.Equal(i2, arena.Allocate());
        Assert.Equal(i1, arena.Allocate());
        Assert.Equal(i0, arena.Allocate());
    }

    [Fact]
    public void Free_AllEntries_ThenReallocateAll()
    {
        var arena = CreateTinyArena();
        var ta = arena.GetTestAccessor();
        const int Count = 8;

        var indices = new int[Count];
        for (var i = 0; i < Count; i++)
            indices[i] = arena.Allocate();

        for (var i = 0; i < Count; i++)
            arena.Free(indices[i]);

        Assert.Equal(0, ta.Count);
        Assert.Equal(Count, ta.HighWater);
        Assert.Equal(Count, ta.FreeCount);

        for (var i = 0; i < Count; i++)
            arena.Allocate();

        Assert.Equal(Count, ta.Count);
        Assert.Equal(Count, ta.HighWater);
        Assert.Equal(0, ta.FreeCount);
    }

    // ═══════════════════════════ Slab boundaries ═════════════════════════

    [Fact]
    public void Allocate_CrossesSlabBoundary_AllocatesNewSlab()
    {
        // slabShift=2 → slabLength=4, directoryLength=4 → 16 total entries possible
        var arena = CreateTinyArena(directoryLength: 4, slabShift: 2);
        var ta = arena.GetTestAccessor();

        // Fill first slab (indices 0-3)
        for (var i = 0; i < 4; i++)
            arena.Allocate();

        Assert.NotNull(ta.Directory[0]);
        Assert.Null(ta.Directory[1]);

        // Allocate one more — crosses into slab 1
        arena.Allocate();

        Assert.NotNull(ta.Directory[1]);
    }

    [Fact]
    public void Allocate_MultipleSlabs_AllAccessible()
    {
        var arena = CreateTinyArena(directoryLength: 4, slabShift: 2);

        // Fill all 4 slabs (16 entries)
        for (var i = 0; i < 16; i++)
        {
            var idx = arena.Allocate();
            arena[idx] = new Int32Entry { Value = idx * 10 };
        }

        for (var i = 0; i < 16; i++)
            Assert.Equal(i * 10, arena[i].Value);
    }

    [Fact]
    public void Allocate_FillsEntireDirectory()
    {
        var arena = CreateTinyArena(directoryLength: 3, slabShift: 1);
        var ta = arena.GetTestAccessor();

        // slabShift=1 → slabLength=2, directoryLength=3 → 6 total entries
        for (var i = 0; i < 6; i++)
            arena.Allocate();

        Assert.Equal(6, ta.Count);
        Assert.Equal(6, ta.HighWater);
        Assert.NotNull(ta.Directory[0]);
        Assert.NotNull(ta.Directory[1]);
        Assert.NotNull(ta.Directory[2]);
    }

#if DEBUG
    [Fact]
    public void Allocate_PastDirectoryEnd_Crashes()
    {
        var arena = CreateTinyArena(directoryLength: 2, slabShift: 1);

        // slabShift=1 → slabLength=2, directoryLength=2 → 4 total entries
        for (var i = 0; i < 4; i++)
            arena.Allocate();

        using var listener = new AssertThrowsListener();
        Assert.ThrowsAny<Exception>(() => arena.Allocate());
    }
#endif

    [Fact]
    public void SlabsOnlyAllocatedOnDemand()
    {
        var arena = CreateTinyArena(directoryLength: 8, slabShift: 2);
        var ta = arena.GetTestAccessor();

        // No allocations yet — all slabs null
        for (var i = 0; i < 8; i++)
            Assert.Null(ta.Directory[i]);

        // Allocate 1 entry — only slab 0 allocated
        arena.Allocate();
        Assert.NotNull(ta.Directory[0]);
        for (var i = 1; i < 8; i++)
            Assert.Null(ta.Directory[i]);
    }

    [Fact]
    public void SlabLength_MatchesExpected()
    {
        var arena = CreateTinyArena(directoryLength: 2, slabShift: 3);
        var ta = arena.GetTestAccessor();
        Assert.Equal(8, ta.SlabLength);
        Assert.Equal(3, ta.SlabShift);
        Assert.Equal(7, ta.SlabMask);
    }

    // ═══════════════════════════ Free + slab interaction ══════════════════

    [Fact]
    public void Free_AcrossSlabs_ReusesMixedIndices()
    {
        var arena = CreateTinyArena(directoryLength: 4, slabShift: 2);

        // Allocate 8 entries across 2 slabs
        var indices = new int[8];
        for (var i = 0; i < 8; i++)
        {
            indices[i] = arena.Allocate();
            arena[indices[i]] = new Int32Entry { Value = i };
        }

        // Free one from each slab
        arena.Free(indices[1]); // slab 0
        arena.Free(indices[5]); // slab 1

        // Reallocate — should reuse freed indices (LIFO: 5 first, then 1)
        var r0 = arena.Allocate();
        var r1 = arena.Allocate();
        Assert.Equal(5, r0);
        Assert.Equal(1, r1);

        // Data at other indices is untouched
        Assert.Equal(0, arena[indices[0]].Value);
        Assert.Equal(2, arena[indices[2]].Value);
        Assert.Equal(7, arena[indices[7]].Value);
    }

    [Fact]
    public void Free_DoesNotClearData()
    {
        var arena = CreateTinyArena();
        var idx = arena.Allocate();
        arena[idx] = new Int32Entry { Value = 42 };
        arena.Free(idx);

        // Re-allocate the same index — stale data is still present
        var reused = arena.Allocate();
        Assert.Equal(idx, reused);
        Assert.Equal(42, arena[reused].Value);
    }

    // ═══════════════════════════ Interleaved alloc/free ═══════════════════

    [Fact]
    public void InterleavedAllocFree_MaintainsCorrectCounts()
    {
        var arena = CreateTinyArena(directoryLength: 8, slabShift: 2);
        var ta = arena.GetTestAccessor();

        var i0 = arena.Allocate();
        var i1 = arena.Allocate();
        var i2 = arena.Allocate();
        Assert.Equal(3, ta.Count);

        arena.Free(i1);
        Assert.Equal(2, ta.Count);
        Assert.Equal(1, ta.FreeCount);

        var i3 = arena.Allocate(); // reuses i1
        Assert.Equal(i1, i3);
        Assert.Equal(3, ta.Count);
        Assert.Equal(0, ta.FreeCount);
        Assert.Equal(3, ta.HighWater);

        var i4 = arena.Allocate(); // fresh bump
        Assert.Equal(3, i4);
        Assert.Equal(4, ta.Count);
        Assert.Equal(4, ta.HighWater);

        arena.Free(i0);
        arena.Free(i2);
        arena.Free(i3);
        arena.Free(i4);
        Assert.Equal(0, ta.Count);
        Assert.Equal(4, ta.FreeCount);
        Assert.Equal(4, ta.HighWater);
    }

    // ═══════════════════════════ Slab shift = 0 (1 entry per slab) ═══════

    [Fact]
    public void SlabShiftZero_OneEntryPerSlab()
    {
        var arena = new UnsafeSlabArena<Int32Entry>(directoryLength: 4, slabShift: 0);
        var ta = arena.GetTestAccessor();

        Assert.Equal(1, ta.SlabLength);
        Assert.Equal(0, ta.SlabMask);

        for (var i = 0; i < 4; i++)
        {
            var idx = arena.Allocate();
            arena[idx] = new Int32Entry { Value = i };
        }

        // Each entry is in its own slab
        for (var i = 0; i < 4; i++)
        {
            Assert.NotNull(ta.Directory[i]);
            Assert.Equal(i, arena[i].Value);
        }
    }

    // ═══════════════════════════ Large batches ════════════════════════════

    [Theory]
    [InlineData(2, 100)]
    [InlineData(3, 500)]
    [InlineData(4, 1000)]
    public void AllocateMany_AllIndicesUnique_AllAccessible(int slabShift, int count)
    {
        var slabLength = 1 << slabShift;
        var directoryLength = (count / slabLength) + 2;
        var arena = new UnsafeSlabArena<Int32Entry>(directoryLength, slabShift);
        var ta = arena.GetTestAccessor();

        var seen = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            var idx = arena.Allocate();
            Assert.True(seen.Add(idx), $"Duplicate index {idx} at allocation {i}.");
            arena[idx] = new Int32Entry { Value = i };
        }

        Assert.Equal(count, ta.Count);
        Assert.Equal(count, ta.HighWater);

        for (var i = 0; i < count; i++)
            Assert.Equal(i, arena[i].Value);
    }

    [Theory]
    [InlineData(2, 100)]
    [InlineData(3, 500)]
    public void AllocateFreeReallocate_Cycles(int slabShift, int count)
    {
        var slabLength = 1 << slabShift;
        var directoryLength = (count / slabLength) + 2;
        var arena = new UnsafeSlabArena<Int32Entry>(directoryLength, slabShift);
        var ta = arena.GetTestAccessor();

        // Allocate all
        var indices = new int[count];
        for (var i = 0; i < count; i++)
            indices[i] = arena.Allocate();

        // Free half (odd indices)
        var freedCount = 0;
        for (var i = 1; i < count; i += 2)
        {
            arena.Free(indices[i]);
            freedCount++;
        }

        Assert.Equal(count - freedCount, ta.Count);
        Assert.Equal(freedCount, ta.FreeCount);

        // Reallocate freed entries
        var reused = new HashSet<int>();
        for (var i = 0; i < freedCount; i++)
        {
            var idx = arena.Allocate();
            reused.Add(idx);
        }

        // All reused indices should be from the freed set
        for (var i = 1; i < count; i += 2)
            Assert.Contains(indices[i], reused);

        Assert.Equal(count, ta.Count);
        Assert.Equal(0, ta.FreeCount);
        Assert.Equal(count, ta.HighWater);
    }

    // ═══════════════════════════ Default constructor ══════════════════════

    [Fact]
    public void DefaultConstructor_UsesExpectedParameters()
    {
        var arena = new UnsafeSlabArena<Int32Entry>();
        var ta = arena.GetTestAccessor();

        Assert.Equal(65_536, ta.DirectoryLength);
        Assert.Equal(SlabSizeHelper<Int32Entry>.SlabLength, ta.SlabLength);
        Assert.Equal(SlabSizeHelper<Int32Entry>.SlabShift, ta.SlabShift);
        Assert.Equal(SlabSizeHelper<Int32Entry>.OffsetMask, ta.SlabMask);
    }

    [Fact]
    public void DefaultConstructor_AllocateAndAccess()
    {
        var arena = new UnsafeSlabArena<Int32Entry>();
        var idx = arena.Allocate();
        arena[idx] = new Int32Entry { Value = 777 };
        Assert.Equal(777, arena[idx].Value);
    }

    // ═══════════════════════════ Debug assertions ═════════════════════════

#if DEBUG
    [Fact]
    public void Debug_MutatingFromDifferentThread_TriggersAssert()
    {
        var arena = CreateTinyArena();
        arena.Allocate(); // establish owner

        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Wrap in a listener that catches Debug.Assert as an exception.
                using var listener = new AssertThrowsListener();
                arena.Allocate();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.Start();
        thread.Join();

        Assert.NotNull(caught);
        Assert.Contains("owner is thread", caught.Message);
    }

    [Fact]
    public void Debug_FreeFromDifferentThread_TriggersAssert()
    {
        var arena = CreateTinyArena();
        var idx = arena.Allocate(); // establish owner

        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var listener = new AssertThrowsListener();
                arena.Free(idx);
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.Start();
        thread.Join();

        Assert.NotNull(caught);
        Assert.Contains("owner is thread", caught.Message);
    }

    /// <summary>Replaces the default trace listener so <see cref="Debug.Assert(bool)"/> failures throw instead of popping a dialog.</summary>
    private sealed class AssertThrowsListener : TraceListener, IDisposable
    {
        public AssertThrowsListener()
        {
            Trace.Listeners.Clear();
            Trace.Listeners.Add(this);
        }

        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }

        public override void Fail(string? message, string? detailMessage)
            => throw new InvalidOperationException(message ?? detailMessage ?? "Debug.Assert failed");

        void IDisposable.Dispose()
        {
            Trace.Listeners.Remove(this);
            Trace.Listeners.Add(new DefaultTraceListener());
        }
    }
#endif
}

// ═══════════════════════════ SlabSizeHelper parity tests ══════════════════

public class SlabSizeHelperTests
{
    [Fact]
    public void SlabLength_IsPowerOfTwo()
    {
        var length = SlabSizeHelper<int>.SlabLength;
        Assert.True(BitOperations.IsPow2(length), $"SlabLength {length} is not a power of 2.");
    }

    [Fact]
    public void SlabShift_IsLog2OfSlabLength()
        => Assert.Equal(
            BitOperations.Log2((uint)SlabSizeHelper<int>.SlabLength),
            SlabSizeHelper<int>.SlabShift);

    [Fact]
    public void OffsetMask_IsSlabLengthMinusOne()
        => Assert.Equal(
            SlabSizeHelper<int>.SlabLength - 1,
            SlabSizeHelper<int>.OffsetMask);

    [Fact]
    public void ArrayStaysUnderLargeObjectHeapThreshold()
    {
        var itemSize = Unsafe.SizeOf<int>();
        var arrayBytes = (SlabSizeHelper<int>.SlabLength * itemSize) + 32;
        Assert.True(arrayBytes < 85_000, $"Array size {arrayBytes} bytes exceeds LOH threshold.");
    }

    [Fact]
    public void NextPowerOfTwo_WouldExceedThreshold()
    {
        var itemSize = Unsafe.SizeOf<int>();
        var doubledLength = SlabSizeHelper<int>.SlabLength * 2;
        var doubledBytes = (doubledLength * itemSize) + 32;
        Assert.True(doubledBytes >= 85_000, $"Doubled array {doubledBytes} bytes still fits — slab is not maximally sized.");
    }

    [Fact]
    public void DifferentItemTypes_ProduceDifferentSlabLengths()
    {
        var small = SlabSizeHelper<byte>.SlabLength;
        var large = SlabSizeHelper<LargePayload>.SlabLength;
        Assert.True(small >= large, $"Smaller item type should yield equal or larger slab length: small={small}, large={large}.");
    }

    [Fact]
    public void SlabLength_IsAtLeastOne()
        => Assert.True(SlabSizeHelper<LargePayload>.SlabLength >= 1);

    [Fact]
    public void OversizedItem_ProducesSlabLengthOfOne()
    {
        Assert.Equal(1, SlabSizeHelper<OversizedPayload>.SlabLength);
        Assert.Equal(0, SlabSizeHelper<OversizedPayload>.SlabShift);
        Assert.Equal(0, SlabSizeHelper<OversizedPayload>.OffsetMask);
    }

    [Fact]
    public void SharedHelper_MatchesPcqHelper()
    {
        Assert.Equal(
            SlabSizeHelper<int>.SlabLength,
            Fabrica.Core.Collections.ProducerConsumerQueue<int>.SlabSizeHelper.SlabLength);
        Assert.Equal(
            SlabSizeHelper<int>.SlabShift,
            Fabrica.Core.Collections.ProducerConsumerQueue<int>.SlabSizeHelper.SlabShift);
        Assert.Equal(
            SlabSizeHelper<int>.OffsetMask,
            Fabrica.Core.Collections.ProducerConsumerQueue<int>.SlabSizeHelper.OffsetMask);
    }

    [StructLayout(LayoutKind.Sequential, Size = 4096)]
    private struct LargePayload;

    [StructLayout(LayoutKind.Sequential, Size = 90_000)]
    private struct OversizedPayload;
}
