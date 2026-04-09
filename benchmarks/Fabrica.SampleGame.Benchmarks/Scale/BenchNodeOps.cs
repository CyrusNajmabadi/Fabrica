using System.Runtime.CompilerServices;
using Fabrica.Core.Memory;
using Fabrica.Core.Memory.Nodes;

namespace Fabrica.SampleGame.Benchmarks.Scale;

internal struct BenchNodeOps : INodeOps<BenchNode>
{
    internal GlobalNodeStore<BenchNode, BenchNodeOps> Store;

    readonly void INodeOps<BenchNode>.EnumerateChildren<TVisitor>(in BenchNode node, ref TVisitor visitor)
    {
        if (node.Child0.IsValid) visitor.Visit(node.Child0);
        if (node.Child1.IsValid) visitor.Visit(node.Child1);
        if (node.Child2.IsValid) visitor.Visit(node.Child2);
        if (node.Child3.IsValid) visitor.Visit(node.Child3);
        if (node.Child4.IsValid) visitor.Visit(node.Child4);
        if (node.Child5.IsValid) visitor.Visit(node.Child5);
        if (node.Child6.IsValid) visitor.Visit(node.Child6);
        if (node.Child7.IsValid) visitor.Visit(node.Child7);
        if (node.Next.IsValid) visitor.Visit(node.Next);
    }

    readonly void INodeOps<BenchNode>.EnumerateRefChildren<TVisitor>(ref BenchNode node, ref TVisitor visitor)
    {
        if (node.Child0 != Handle<BenchNode>.None) visitor.VisitRef(ref node.Child0);
        if (node.Child1 != Handle<BenchNode>.None) visitor.VisitRef(ref node.Child1);
        if (node.Child2 != Handle<BenchNode>.None) visitor.VisitRef(ref node.Child2);
        if (node.Child3 != Handle<BenchNode>.None) visitor.VisitRef(ref node.Child3);
        if (node.Child4 != Handle<BenchNode>.None) visitor.VisitRef(ref node.Child4);
        if (node.Child5 != Handle<BenchNode>.None) visitor.VisitRef(ref node.Child5);
        if (node.Child6 != Handle<BenchNode>.None) visitor.VisitRef(ref node.Child6);
        if (node.Child7 != Handle<BenchNode>.None) visitor.VisitRef(ref node.Child7);
        if (node.Next != Handle<BenchNode>.None) visitor.VisitRef(ref node.Next);
    }

    readonly void INodeOps<BenchNode>.IncrementChildRefCounts(in BenchNode node)
    {
        var refCounts = Store.RefCounts;
        if (node.Child0.IsValid) refCounts.Increment(node.Child0);
        if (node.Child1.IsValid) refCounts.Increment(node.Child1);
        if (node.Child2.IsValid) refCounts.Increment(node.Child2);
        if (node.Child3.IsValid) refCounts.Increment(node.Child3);
        if (node.Child4.IsValid) refCounts.Increment(node.Child4);
        if (node.Child5.IsValid) refCounts.Increment(node.Child5);
        if (node.Child6.IsValid) refCounts.Increment(node.Child6);
        if (node.Child7.IsValid) refCounts.Increment(node.Child7);
        if (node.Next.IsValid) refCounts.Increment(node.Next);
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
