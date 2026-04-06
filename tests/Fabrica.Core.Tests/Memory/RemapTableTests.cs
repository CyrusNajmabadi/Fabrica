using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

public class RemapTableTests
{
    // ═══════════════════════════ Construction ═════════════════════════════

    [Fact]
    public void Constructor_SetsThreadCount()
    {
        var table = new RemapTable(4);
        Assert.Equal(4, table.ThreadCount);
    }

    [Fact]
    public void Constructor_AllThreadsStartEmpty()
    {
        var table = new RemapTable(3);
        for (var i = 0; i < 3; i++)
            Assert.Equal(0, table.Count(i));
    }

    // ═══════════════════════════ SetMapping / Resolve ═════════════════════

    [Fact]
    public void SetMapping_Resolve_RoundTrips()
    {
        var table = new RemapTable(2);
        table.SetMapping(threadId: 0, localIndex: 0, globalIndex: 100);
        table.SetMapping(threadId: 0, localIndex: 1, globalIndex: 101);
        table.SetMapping(threadId: 1, localIndex: 0, globalIndex: 200);

        Assert.Equal(100, table.Resolve(0, 0));
        Assert.Equal(101, table.Resolve(0, 1));
        Assert.Equal(200, table.Resolve(1, 0));
    }

    [Fact]
    public void SetMapping_UpdatesCount()
    {
        var table = new RemapTable(2);
        table.SetMapping(0, 0, 10);
        table.SetMapping(0, 1, 11);
        table.SetMapping(1, 0, 20);

        Assert.Equal(2, table.Count(0));
        Assert.Equal(1, table.Count(1));
    }

    [Fact]
    public void SetMapping_ContiguousBatch_AllResolvable()
    {
        var table = new RemapTable(1);
        const int StartGlobal = 50;
        const int Count = 10;

        for (var i = 0; i < Count; i++)
            table.SetMapping(0, i, StartGlobal + i);

        for (var i = 0; i < Count; i++)
            Assert.Equal(StartGlobal + i, table.Resolve(0, i));
    }

    // ═══════════════════════════ Reset ════════════════════════════════════

    [Fact]
    public void Reset_ClearsAllMappings()
    {
        var table = new RemapTable(2);
        table.SetMapping(0, 0, 100);
        table.SetMapping(1, 0, 200);
        table.SetMapping(1, 1, 201);

        table.Reset();

        Assert.Equal(0, table.Count(0));
        Assert.Equal(0, table.Count(1));
    }

    [Fact]
    public void Reset_AllowsReuse()
    {
        var table = new RemapTable(2);
        table.SetMapping(0, 0, 100);
        table.SetMapping(1, 0, 200);

        table.Reset();

        table.SetMapping(0, 0, 300);
        table.SetMapping(1, 0, 400);
        table.SetMapping(1, 1, 401);

        Assert.Equal(300, table.Resolve(0, 0));
        Assert.Equal(400, table.Resolve(1, 0));
        Assert.Equal(401, table.Resolve(1, 1));
    }

    // ═══════════════════════════ Multi-thread interleaved ═════════════════

    [Fact]
    public void MultipleThreads_IndependentMappings()
    {
        var table = new RemapTable(4);

        for (var t = 0; t < 4; t++)
            for (var i = 0; i < 5; i++)
                table.SetMapping(t, i, (t * 100) + i);

        for (var t = 0; t < 4; t++)
        {
            Assert.Equal(5, table.Count(t));
            for (var i = 0; i < 5; i++)
                Assert.Equal((t * 100) + i, table.Resolve(t, i));
        }
    }
}
