using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;

namespace Fabrica.SampleGame.Benchmarks.Scale;

/// <summary>
/// Creates 14 new nodes (10 chain + 4 spine collectors) along a single path from root to leaf,
/// referencing existing global handles for the 7 unchanged siblings at each level.
/// </summary>
internal sealed class SpineUpdateJob : Job
{
    internal ThreadLocalBuffer<BenchNode>[]? Buffers;
    internal UnsafeSlabArena<BenchNode> Arena = null!;

    /// <summary>Global handle of the current root collector.</summary>
    internal Handle<BenchNode> CurrentRoot;

    /// <summary>Which child index (0-7) to replace at each level. Set before each tick.</summary>
    internal int PathIndex0, PathIndex1, PathIndex2, PathIndex3;

    internal int NewLeafValue;

    /// <summary>The new root handle after execution (local, remapped during merge).</summary>
    internal Handle<BenchNode> NewRoot;

    protected internal override void Execute(JobContext context)
    {
        var buf = Buffers![context.WorkerIndex];

        var newChainHead = BuildNewLeafChain(buf, 10, NewLeafValue);

        var oldL0 = Arena[CurrentRoot];
        var oldL1Handle = GetChild(in oldL0, PathIndex0);
        var oldL1 = Arena[oldL1Handle];
        var oldL2Handle = GetChild(in oldL1, PathIndex1);
        var oldL2 = Arena[oldL2Handle];
        var oldL3Handle = GetChild(in oldL2, PathIndex2);
        var oldL3 = Arena[oldL3Handle];

        var newL3Handle = buf.Allocate(ReplaceChild(in oldL3, PathIndex3, newChainHead));
        var newL2Handle = buf.Allocate(ReplaceChild(in oldL2, PathIndex2, newL3Handle));
        var newL1Handle = buf.Allocate(ReplaceChild(in oldL1, PathIndex1, newL2Handle));
        var newL0Handle = buf.Allocate(ReplaceChild(in oldL0, PathIndex0, newL1Handle), isRoot: true);

        NewRoot = newL0Handle;
    }

    private static Handle<BenchNode> BuildNewLeafChain(ThreadLocalBuffer<BenchNode> buf, int length, int startValue)
    {
        var next = Handle<BenchNode>.None;
        for (var i = length - 1; i >= 0; i--)
        {
            var h = buf.Allocate(new BenchNode { Next = next, Value = startValue + i });
            next = h;
        }

        return next;
    }

    private static Handle<BenchNode> GetChild(in BenchNode node, int index) => index switch
    {
        0 => node.Child0,
        1 => node.Child1,
        2 => node.Child2,
        3 => node.Child3,
        4 => node.Child4,
        5 => node.Child5,
        6 => node.Child6,
        7 => node.Child7,
        _ => Handle<BenchNode>.None,
    };

    private static BenchNode ReplaceChild(in BenchNode source, int index, Handle<BenchNode> newChild)
    {
        var copy = source;
        switch (index)
        {
            case 0: copy.Child0 = newChild; break;
            case 1: copy.Child1 = newChild; break;
            case 2: copy.Child2 = newChild; break;
            case 3: copy.Child3 = newChild; break;
            case 4: copy.Child4 = newChild; break;
            case 5: copy.Child5 = newChild; break;
            case 6: copy.Child6 = newChild; break;
            case 7: copy.Child7 = newChild; break;
        }

        return copy;
    }

    protected override void ResetState()
    {
        Buffers = null;
        Arena = null!;
        CurrentRoot = default;
        NewRoot = default;
    }
}
