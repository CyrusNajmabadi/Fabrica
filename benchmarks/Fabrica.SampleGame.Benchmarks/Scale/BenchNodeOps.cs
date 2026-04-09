using System.Runtime.CompilerServices;
using Fabrica.Core.Memory;
using Fabrica.Core.Memory.Nodes;

namespace Fabrica.SampleGame.Benchmarks.Scale;

internal struct BenchNodeOps : INodeOps<BenchNode>
{
    internal GlobalNodeStore<BenchNode, BenchNodeOps> Store;

    readonly void INodeOps<BenchNode>.EnumerateChildren<TVisitor>(in BenchNode node, ref TVisitor visitor)
    {
        visitor.Visit(node.Child0);
        visitor.Visit(node.Child1);
        visitor.Visit(node.Child2);
        visitor.Visit(node.Child3);
        visitor.Visit(node.Child4);
        visitor.Visit(node.Child5);
        visitor.Visit(node.Child6);
        visitor.Visit(node.Child7);
        visitor.Visit(node.Next);
    }

    readonly void INodeOps<BenchNode>.EnumerateRefChildren<TVisitor>(ref BenchNode node, ref TVisitor visitor)
    {
        visitor.VisitRef(ref node.Child0);
        visitor.VisitRef(ref node.Child1);
        visitor.VisitRef(ref node.Child2);
        visitor.VisitRef(ref node.Child3);
        visitor.VisitRef(ref node.Child4);
        visitor.VisitRef(ref node.Child5);
        visitor.VisitRef(ref node.Child6);
        visitor.VisitRef(ref node.Child7);
        visitor.VisitRef(ref node.Next);
    }

    readonly void INodeOps<BenchNode>.IncrementChildRefCounts(in BenchNode node)
    {
        var refCounts = Store.RefCounts;
        refCounts.Increment(node.Child0);
        refCounts.Increment(node.Child1);
        refCounts.Increment(node.Child2);
        refCounts.Increment(node.Child3);
        refCounts.Increment(node.Child4);
        refCounts.Increment(node.Child5);
        refCounts.Increment(node.Child6);
        refCounts.Increment(node.Child7);
        refCounts.Increment(node.Next);
    }

    public readonly void VisitRef<T>(ref Handle<T> handle) where T : struct
    {
        if (typeof(T) == typeof(BenchNode))
            handle = Store.RemapHandle(handle);
    }

    public readonly void Visit<T>(Handle<T> handle) where T : struct
    {
        if (typeof(T) == typeof(BenchNode))
            Store.DecrementRefCount(Unsafe.As<Handle<T>, Handle<BenchNode>>(ref handle));
    }
}
