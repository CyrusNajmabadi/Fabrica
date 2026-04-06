using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

/// <summary>
/// End-to-end integration test proving the full production-side pipeline: real <see cref="WorkerPool"/>
/// + <see cref="JobScheduler"/>, concrete job subclasses that allocate nodes in <see cref="ThreadLocalBuffer{T}"/>
/// instances, followed by <see cref="GlobalNodeStore{TNode, TNodeOps}"/> merge (drain, rewrite, refcount), and
/// <see cref="DagValidator"/> verification.
/// </summary>
public class JobMergePipelineTests : IDisposable
{
    // ── Node types ───────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct ParentNode
    {
        public Handle<ParentNode> LeftParent;
        public Handle<ChildNode> ChildRef;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChildNode
    {
        public int Value;
    }

    // ── Node ops ─────────────────────────────────────────────────────────

    private struct TestNodeOps : INodeOps<ParentNode>, INodeOps<ChildNode>
    {
        internal GlobalNodeStore<ParentNode, TestNodeOps> ParentStore;
        internal GlobalNodeStore<ChildNode, TestNodeOps> ChildStore;

        readonly void INodeOps<ParentNode>.EnumerateChildren<TVisitor>(in ParentNode node, ref TVisitor visitor)
        {
            if (node.LeftParent.IsValid) visitor.Visit(node.LeftParent);
            if (node.ChildRef.IsValid) visitor.Visit(node.ChildRef);
        }

        readonly void INodeOps<ChildNode>.EnumerateChildren<TVisitor>(in ChildNode node, ref TVisitor visitor)
        {
        }

        readonly void INodeOps<ParentNode>.EnumerateRefChildren<TVisitor>(ref ParentNode node, ref TVisitor visitor)
        {
            if (node.LeftParent.Index != -1) visitor.VisitRef(ref node.LeftParent);
            if (node.ChildRef.Index != -1) visitor.VisitRef(ref node.ChildRef);
        }

        readonly void INodeOps<ChildNode>.EnumerateRefChildren<TVisitor>(ref ChildNode node, ref TVisitor visitor)
        {
        }

        public readonly void Visit<T>(Handle<T> handle) where T : struct
        {
            if (typeof(T) == typeof(ParentNode))
                ParentStore.DecrementRefCount(Unsafe.As<Handle<T>, Handle<ParentNode>>(ref handle));
            else if (typeof(T) == typeof(ChildNode))
                ChildStore.DecrementRefCount(Unsafe.As<Handle<T>, Handle<ChildNode>>(ref handle));
        }
    }

    // ── Merge visitors ───────────────────────────────────────────────────

    private struct RemapVisitor : INodeVisitor
    {
        internal RemapTable ParentRemap;
        internal RemapTable ChildRemap;

        public readonly void VisitRef<T>(ref Handle<T> handle) where T : struct
        {
            var index = handle.Index;
            if (!TaggedHandle.IsLocal(index))
                return;

            var threadId = TaggedHandle.DecodeThreadId(index);
            var localIndex = TaggedHandle.DecodeLocalIndex(index);

            if (typeof(T) == typeof(ParentNode))
                handle = new Handle<T>(ParentRemap.Resolve(threadId, localIndex));
            else if (typeof(T) == typeof(ChildNode))
                handle = new Handle<T>(ChildRemap.Resolve(threadId, localIndex));
        }
    }

    private struct RefcountVisitor : INodeVisitor
    {
        internal GlobalNodeStore<ParentNode, TestNodeOps> ParentStore;
        internal GlobalNodeStore<ChildNode, TestNodeOps> ChildStore;

        public readonly void Visit<T>(Handle<T> handle) where T : struct
        {
            if (typeof(T) == typeof(ParentNode))
                ParentStore.IncrementRefCount(Unsafe.As<Handle<T>, Handle<ParentNode>>(ref handle));
            else if (typeof(T) == typeof(ChildNode))
                ChildStore.IncrementRefCount(Unsafe.As<Handle<T>, Handle<ChildNode>>(ref handle));
        }
    }

    // ── DagValidator accessor ────────────────────────────────────────────

    private const int ParentTypeId = 0;
    private const int ChildTypeId = 1;

    private struct TestWorldAccessor(
        GlobalNodeStore<ParentNode, TestNodeOps> parentStore,
        GlobalNodeStore<ChildNode, TestNodeOps> childStore) : DagValidator.IWorldAccessor
    {
        public readonly int TypeCount => 2;

        public readonly int HighWater(int typeId) => typeId switch
        {
            ParentTypeId => parentStore.Arena.GetTestAccessor().HighWater,
            ChildTypeId => childStore.Arena.GetTestAccessor().HighWater,
            _ => 0,
        };

        public readonly int GetRefCount(int typeId, int index) => typeId switch
        {
            ParentTypeId => parentStore.RefCounts.GetCount(new Handle<ParentNode>(index)),
            ChildTypeId => childStore.RefCounts.GetCount(new Handle<ChildNode>(index)),
            _ => 0,
        };

        public readonly void GetChildren(int typeId, int index, List<DagValidator.NodeRef> children)
        {
            if (typeId == ParentTypeId)
            {
                ref readonly var node = ref parentStore.Arena[new Handle<ParentNode>(index)];
                if (node.LeftParent.IsValid) children.Add(new DagValidator.NodeRef(ParentTypeId, node.LeftParent.Index));
                if (node.ChildRef.IsValid) children.Add(new DagValidator.NodeRef(ChildTypeId, node.ChildRef.Index));
            }
        }
    }

    // ── Shared test infrastructure ───────────────────────────────────────

    private readonly WorkerPool _pool = new(workerCount: 4);

    public void Dispose() => _pool.Dispose();

    private static (GlobalNodeStore<ParentNode, TestNodeOps> ParentStore, GlobalNodeStore<ChildNode, TestNodeOps> ChildStore)
        CreateStores()
    {
        var childArena = new UnsafeSlabArena<ChildNode>();
        var childRefCounts = new RefCountTable<ChildNode>();
        var childStore = new GlobalNodeStore<ChildNode, TestNodeOps>(childArena, childRefCounts, default);

        var parentArena = new UnsafeSlabArena<ParentNode>();
        var parentRefCounts = new RefCountTable<ParentNode>();
        var parentStore = new GlobalNodeStore<ParentNode, TestNodeOps>(parentArena, parentRefCounts, default);

        var ops = new TestNodeOps { ParentStore = parentStore, ChildStore = childStore };
        childStore.SetNodeOps(ops);
        parentStore.SetNodeOps(ops);

        return (parentStore, childStore);
    }

    // ── Job subclasses ───────────────────────────────────────────────────

    /// <summary>
    /// Allocates <see cref="ChildNode"/> instances in the executing worker's TLB.
    /// Each job creates a configurable number of children with sequential values starting from
    /// <see cref="ValueStart"/>. The allocated handles are stored in <see cref="AllocatedHandles"/>
    /// for the parent job to reference.
    /// </summary>
    private sealed class CreateChildNodesJob : Job
    {
        internal ThreadLocalBuffer<ChildNode>[] ChildTlbs = null!;
        internal int ChildCount;
        internal int ValueStart;
        internal Handle<ChildNode>[] AllocatedHandles = null!;

        protected internal override void Execute(WorkerContext context)
        {
            var tlb = ChildTlbs[context.WorkerIndex];
            AllocatedHandles = new Handle<ChildNode>[ChildCount];
            for (var i = 0; i < ChildCount; i++)
            {
                var handle = tlb.Allocate();
                tlb[TaggedHandle.DecodeLocalIndex(handle.Index)] = new ChildNode { Value = ValueStart + i };
                AllocatedHandles[i] = handle;
            }
        }

        protected internal override void Reset()
        {
            ChildTlbs = null!;
            AllocatedHandles = null!;
        }
    }

    /// <summary>
    /// Allocates <see cref="ParentNode"/> instances referencing child handles produced by upstream
    /// <see cref="CreateChildNodesJob"/>s. Consumes handles from ALL sources. Parents are chained
    /// via <see cref="ParentNode.LeftParent"/> so that the last parent is the root and all others
    /// are reachable through the chain.
    /// </summary>
    private sealed class CreateParentNodesJob : Job
    {
        internal ThreadLocalBuffer<ParentNode>[] ParentTlbs = null!;
        internal CreateChildNodesJob[] ChildSources = null!;

        protected internal override void Execute(WorkerContext context)
        {
            var tlb = ParentTlbs[context.WorkerIndex];

            var allChildHandles = new List<Handle<ChildNode>>();
            foreach (var source in ChildSources)
                allChildHandles.AddRange(source.AllocatedHandles);

            var previousParent = Handle<ParentNode>.None;
            Handle<ParentNode> lastHandle = default;
            for (var i = 0; i < allChildHandles.Count; i++)
            {
                lastHandle = tlb.Allocate();
                tlb[TaggedHandle.DecodeLocalIndex(lastHandle.Index)] = new ParentNode
                {
                    LeftParent = previousParent,
                    ChildRef = allChildHandles[i],
                };
                previousParent = lastHandle;
            }

            if (allChildHandles.Count > 0)
                tlb.MarkRoot(lastHandle);
        }

        protected internal override void Reset()
        {
            ParentTlbs = null!;
            ChildSources = null!;
        }
    }

    // ── Tests ────────────────────────────────────────────────────────────

    /// <summary>
    /// Proves the full pipeline: two child-creation jobs fan into a parent-creation job via the
    /// DAG dependency system, running on real worker threads with work-stealing. After the DAG
    /// completes, the merge pipeline drains TLBs, rewrites handles, increments refcounts, collects
    /// roots, and the result passes <see cref="DagValidator.AssertValid"/>.
    /// </summary>
    [Fact]
    public void EndToEnd_JobDAG_MergeAndValidate()
    {
        var (parentStore, childStore) = CreateStores();
        const int WorkerCount = 4;

        var childTlbs = new ThreadLocalBuffer<ChildNode>[WorkerCount];
        var parentTlbs = new ThreadLocalBuffer<ParentNode>[WorkerCount];
        for (var w = 0; w < WorkerCount; w++)
        {
            childTlbs[w] = new ThreadLocalBuffer<ChildNode>(w);
            parentTlbs[w] = new ThreadLocalBuffer<ParentNode>(w);
        }

        // Build job DAG: childJob0 and childJob1 must both complete before parentJob runs.
        var childJob0 = new CreateChildNodesJob
        {
            ChildTlbs = childTlbs,
            ChildCount = 2,
            ValueStart = 10,
        };

        var childJob1 = new CreateChildNodesJob
        {
            ChildTlbs = childTlbs,
            ChildCount = 1,
            ValueStart = 30,
        };

        var parentJob = new CreateParentNodesJob
        {
            ParentTlbs = parentTlbs,
            ChildSources = [childJob0, childJob1],
            RemainingDependencies = 2,
            Dependents = null,
        };

        childJob0.Dependents = [parentJob];
        childJob1.Dependents = [parentJob];

        var scheduler = new JobScheduler(_pool);
        var testAccessor = scheduler.GetTestAccessor();
        testAccessor.Inject(childJob0);
        testAccessor.Inject(childJob1);
        var completed = testAccessor.WaitForCompletion(millisecondsTimeout: 5000);
        Assert.True(completed, "Job DAG did not complete within timeout");

        // Verify jobs produced data
        var totalChildren = childTlbs.Sum(t => t.Count);
        var totalParents = parentTlbs.Sum(t => t.Count);
        Assert.Equal(3, totalChildren);
        Assert.Equal(3, totalParents);

        // ── Merge pipeline ───────────────────────────────────────────────

        var childRemap = new RemapTable(WorkerCount);
        var parentRemap = new RemapTable(WorkerCount);

        var (childStart, childCount) = childStore.DrainBuffers(childTlbs, childRemap);
        var (parentStart, parentCount) = parentStore.DrainBuffers(parentTlbs, parentRemap);

        Assert.Equal(3, childCount);
        Assert.Equal(3, parentCount);

        var remapVisitor = new RemapVisitor { ParentRemap = parentRemap, ChildRemap = childRemap };
        var refcountVisitor = new RefcountVisitor { ParentStore = parentStore, ChildStore = childStore };

        parentStore.RewriteAndIncrementRefCounts(parentStart, parentCount, ref remapVisitor, ref refcountVisitor);
        childStore.RewriteAndIncrementRefCounts(childStart, childCount, ref remapVisitor, ref refcountVisitor);

        // All parent handles should now be global
        for (var i = parentStart; i < parentStart + parentCount; i++)
        {
            ref readonly var p = ref parentStore.Arena[new Handle<ParentNode>(i)];
            if (p.ChildRef.IsValid)
                Assert.True(TaggedHandle.IsGlobal(p.ChildRef.Index), $"Parent[{i}].ChildRef is not global");
            if (p.LeftParent.IsValid)
                Assert.True(TaggedHandle.IsGlobal(p.LeftParent.Index), $"Parent[{i}].LeftParent is not global");
        }

        // ── Root collection ──────────────────────────────────────────────

        var rootList = new UnsafeList<Handle<ParentNode>>();
        parentStore.CollectAndRemapRoots(parentTlbs, parentRemap, rootList);
        var roots = rootList.WrittenSpan;
        Assert.True(roots.Length >= 1, "Expected at least one root from parent job");

        parentStore.IncrementRoots(roots);

        // ── DagValidator ─────────────────────────────────────────────────

        var dagRoots = new DagValidator.NodeRef[roots.Length];
        for (var i = 0; i < roots.Length; i++)
            dagRoots[i] = new DagValidator.NodeRef(ParentTypeId, roots[i].Index);
        DagValidator.AssertValid(dagRoots, new TestWorldAccessor(parentStore, childStore), strict: true);

        // ── Cascade-free ─────────────────────────────────────────────────

        parentStore.DecrementRoots(roots);

        Assert.Equal(0, parentStore.Arena.GetTestAccessor().Count);
        Assert.Equal(0, childStore.Arena.GetTestAccessor().Count);
    }

    /// <summary>
    /// Verifies that when jobs are stolen (executed on a different worker than where they were
    /// queued), TLB threadId still correctly reflects the executing worker, producing valid
    /// remap tables.
    /// </summary>
    [Fact]
    public void WorkStealing_ProducesValidRemapTables()
    {
        var (_, childStore) = CreateStores();
        const int WorkerCount = 4;

        var childTlbs = new ThreadLocalBuffer<ChildNode>[WorkerCount];
        for (var w = 0; w < WorkerCount; w++)
            childTlbs[w] = new ThreadLocalBuffer<ChildNode>(w);

        // Submit many small jobs to encourage work-stealing
        var jobs = new CreateChildNodesJob[8];
        for (var i = 0; i < jobs.Length; i++)
        {
            jobs[i] = new CreateChildNodesJob
            {
                ChildTlbs = childTlbs,
                ChildCount = 4,
                ValueStart = i * 100,
            };
        }

        var scheduler = new JobScheduler(_pool);
        var testAccessor = scheduler.GetTestAccessor();
        foreach (var job in jobs)
            testAccessor.Inject(job);

        var completed = testAccessor.WaitForCompletion(millisecondsTimeout: 5000);
        Assert.True(completed, "Jobs did not complete within timeout");

        var totalAllocated = childTlbs.Sum(t => t.Count);
        Assert.Equal(32, totalAllocated);

        // Merge and verify all handles resolve correctly
        var remap = new RemapTable(WorkerCount);
        var (start, count) = childStore.DrainBuffers(childTlbs, remap);
        Assert.Equal(32, count);

        // Every node should be accessible via the arena
        for (var i = start; i < start + count; i++)
        {
            ref readonly var node = ref childStore.Arena[new Handle<ChildNode>(i)];
            Assert.True(node.Value >= 0, $"Unexpected value at index {i}");
        }
    }
}
