using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class NonCopyableRemapTableTests
{
    // ═══════════════════════════ Construction ═════════════════════════════

    [Fact]
    public void Constructor_SetsThreadCount()
    {
        var table = new NonCopyableRemapTable(4);
        Assert.Equal(4, table.ThreadCount);
    }

    // ═══════════════════════════ SetMapping / Resolve ═════════════════════

    [Fact]
    public void SetMapping_Resolve_RoundTrips()
    {
        var table = new NonCopyableRemapTable(2);
        table.SetMapping(threadId: 0, localIndex: 0, globalIndex: 100);
        table.SetMapping(threadId: 0, localIndex: 1, globalIndex: 101);
        table.SetMapping(threadId: 1, localIndex: 0, globalIndex: 200);

        var ro = table.AsReadOnly();
        Assert.Equal(100, ro.Resolve(0, 0));
        Assert.Equal(101, ro.Resolve(0, 1));
        Assert.Equal(200, ro.Resolve(1, 0));
    }

    [Fact]
    public void SetMapping_ContiguousBatch_AllResolvable()
    {
        var table = new NonCopyableRemapTable(1);
        const int StartGlobal = 50;
        const int Count = 10;

        for (var i = 0; i < Count; i++)
            table.SetMapping(0, i, StartGlobal + i);

        var ro = table.AsReadOnly();
        for (var i = 0; i < Count; i++)
            Assert.Equal(StartGlobal + i, ro.Resolve(0, i));
    }

    // ═══════════════════════════ Reset ════════════════════════════════════

    [Fact]
    public void Reset_AllowsReuse()
    {
        var table = new NonCopyableRemapTable(2);
        table.SetMapping(0, 0, 100);
        table.SetMapping(1, 0, 200);

        table.Reset();

        table.SetMapping(0, 0, 300);
        table.SetMapping(1, 0, 400);
        table.SetMapping(1, 1, 401);

        var ro = table.AsReadOnly();
        Assert.Equal(300, ro.Resolve(0, 0));
        Assert.Equal(400, ro.Resolve(1, 0));
        Assert.Equal(401, ro.Resolve(1, 1));
    }

    // ═══════════════════════════ Multi-thread interleaved ═════════════════

    [Fact]
    public void MultipleThreads_IndependentMappings()
    {
        var table = new NonCopyableRemapTable(4);

        for (var threadId = 0; threadId < 4; threadId++)
            for (var i = 0; i < 5; i++)
                table.SetMapping(threadId, i, (threadId * 100) + i);

        var ro = table.AsReadOnly();
        for (var threadId = 0; threadId < 4; threadId++)
            for (var i = 0; i < 5; i++)
                Assert.Equal((threadId * 100) + i, ro.Resolve(threadId, i));
    }
}
