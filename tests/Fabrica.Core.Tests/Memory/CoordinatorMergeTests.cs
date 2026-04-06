using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

/// <summary>
/// Tests the 3-phase coordinator merge pipeline: Phase 1 (allocate + copy + remap),
/// Phase 2a (fixup: rewrite local handles to global), Phase 2b (refcount: increment children).
/// Uses a 2-type cross-thread scenario with <see cref="ParentNode"/> and <see cref="ChildNode"/>.
/// </summary>
public class CoordinatorMergeTests
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

    // ── Node ops — single struct implementing INodeOps for both types ───

    private struct MergeNodeOps : INodeOps<ParentNode>, INodeOps<ChildNode>
    {
        internal NodeStore<ParentNode, MergeNodeOps> ParentStore;
        internal NodeStore<ChildNode, MergeNodeOps> ChildStore;

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

    // ── Phase 2a visitor: rewrite local handles to global ────────────────

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

    // ── Phase 2b visitor: increment refcounts for children ───────────────

    private struct RefcountVisitor : INodeVisitor
    {
        internal NodeStore<ParentNode, MergeNodeOps> ParentStore;
        internal NodeStore<ChildNode, MergeNodeOps> ChildStore;

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

    private struct MergeWorldAccessor(
        NodeStore<ParentNode, MergeNodeOps> parentStore,
        NodeStore<ChildNode, MergeNodeOps> childStore) : DagValidator.IWorldAccessor
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

    // ── Store creation ───────────────────────────────────────────────────

    private static (NodeStore<ParentNode, MergeNodeOps> ParentStore, NodeStore<ChildNode, MergeNodeOps> ChildStore)
        CreateStores()
    {
        var childArena = new UnsafeSlabArena<ChildNode>();
        var childRefCounts = new RefCountTable<ChildNode>();
        var childStore = new NodeStore<ChildNode, MergeNodeOps>(childArena, childRefCounts, default);

        var parentArena = new UnsafeSlabArena<ParentNode>();
        var parentRefCounts = new RefCountTable<ParentNode>();
        var parentStore = new NodeStore<ParentNode, MergeNodeOps>(parentArena, parentRefCounts, default);

        var ops = new MergeNodeOps { ParentStore = parentStore, ChildStore = childStore };
        childStore.SetNodeOps(ops);
        parentStore.SetNodeOps(ops);

        return (parentStore, childStore);
    }

    // ═══════════════════════════ Phase 1 tests ═══════════════════════════

    [Fact]
    public void Phase1_CopiesData_BuildsRemap()
    {
        var (_, childStore) = CreateStores();
        const int ThreadCount = 2;

        var tlbs = new ThreadLocalBuffer<ChildNode>[ThreadCount];
        tlbs[0] = new ThreadLocalBuffer<ChildNode>(0);
        tlbs[1] = new ThreadLocalBuffer<ChildNode>(1);

        var c0 = tlbs[0].Allocate();
        tlbs[0][TaggedHandle.DecodeLocalIndex(c0.Index)] = new ChildNode { Value = 10 };

        var c1 = tlbs[1].Allocate();
        tlbs[1][TaggedHandle.DecodeLocalIndex(c1.Index)] = new ChildNode { Value = 20 };

        var c2 = tlbs[1].Allocate();
        tlbs[1][TaggedHandle.DecodeLocalIndex(c2.Index)] = new ChildNode { Value = 30 };

        var remap = new RemapTable(ThreadCount);
        var (start, count) = MergePipeline.DrainBuffers(childStore.Arena, childStore.RefCounts, tlbs, remap);

        Assert.Equal(0, start);
        Assert.Equal(3, count);

        Assert.Equal(10, childStore.Arena[new Handle<ChildNode>(0)].Value);
        Assert.Equal(20, childStore.Arena[new Handle<ChildNode>(1)].Value);
        Assert.Equal(30, childStore.Arena[new Handle<ChildNode>(2)].Value);

        Assert.Equal(0, remap.Resolve(0, 0));
        Assert.Equal(1, remap.Resolve(1, 0));
        Assert.Equal(2, remap.Resolve(1, 1));
    }

    [Fact]
    public void Phase1_EmptyTlb_IsNoOp()
    {
        var (_, childStore) = CreateStores();
        const int ThreadCount = 2;

        var tlbs = new ThreadLocalBuffer<ChildNode>[ThreadCount];
        tlbs[0] = new ThreadLocalBuffer<ChildNode>(0);
        tlbs[1] = new ThreadLocalBuffer<ChildNode>(1);

        var remap = new RemapTable(ThreadCount);
        var (start, count) = MergePipeline.DrainBuffers(childStore.Arena, childStore.RefCounts, tlbs, remap);

        Assert.Equal(0, start);
        Assert.Equal(0, count);
        Assert.Equal(0, childStore.Arena.GetTestAccessor().HighWater);
    }

    [Fact]
    public void Phase1_PartiallyEmptyTlbs_OnlyPopulatedThreadsContribute()
    {
        var (_, childStore) = CreateStores();
        const int ThreadCount = 3;

        var tlbs = new ThreadLocalBuffer<ChildNode>[ThreadCount];
        tlbs[0] = new ThreadLocalBuffer<ChildNode>(0);
        tlbs[1] = new ThreadLocalBuffer<ChildNode>(1);
        tlbs[2] = new ThreadLocalBuffer<ChildNode>(2);

        var c0 = tlbs[0].Allocate();
        tlbs[0][TaggedHandle.DecodeLocalIndex(c0.Index)] = new ChildNode { Value = 10 };

        // Thread 1 is empty

        var c2 = tlbs[2].Allocate();
        tlbs[2][TaggedHandle.DecodeLocalIndex(c2.Index)] = new ChildNode { Value = 30 };

        var remap = new RemapTable(ThreadCount);
        var (start, count) = MergePipeline.DrainBuffers(childStore.Arena, childStore.RefCounts, tlbs, remap);

        Assert.Equal(0, start);
        Assert.Equal(2, count);

        Assert.Equal(10, childStore.Arena[new Handle<ChildNode>(0)].Value);
        Assert.Equal(30, childStore.Arena[new Handle<ChildNode>(1)].Value);

        Assert.Equal(0, remap.Resolve(0, 0));
        Assert.Equal(0, remap.Count(1));
        Assert.Equal(1, remap.Resolve(2, 0));
    }

    // ═══════════════════════════ Phase 2a tests ══════════════════════════

    [Fact]
    public void Phase2a_RemapsLocalHandlesToGlobal()
    {
        var (parentStore, childStore) = CreateStores();

        var childTlbs = new[] { new ThreadLocalBuffer<ChildNode>(0) };
        var parentTlbs = new[] { new ThreadLocalBuffer<ParentNode>(0) };

        var childHandle = childTlbs[0].Allocate();
        childTlbs[0][TaggedHandle.DecodeLocalIndex(childHandle.Index)] = new ChildNode { Value = 42 };

        var parentHandle = parentTlbs[0].Allocate();
        parentTlbs[0][TaggedHandle.DecodeLocalIndex(parentHandle.Index)] = new ParentNode
        {
            LeftParent = Handle<ParentNode>.None,
            ChildRef = childHandle,
        };

        var childRemap = new RemapTable(1);
        var parentRemap = new RemapTable(1);

        MergePipeline.DrainBuffers(childStore.Arena, childStore.RefCounts, childTlbs, childRemap);
        var (parentStart, parentCount) = MergePipeline.DrainBuffers(parentStore.Arena, parentStore.RefCounts, parentTlbs, parentRemap);

        var remapVisitor = new RemapVisitor { ParentRemap = parentRemap, ChildRemap = childRemap };
        var nodeOps = new MergeNodeOps { ParentStore = parentStore, ChildStore = childStore };

        MergePipeline.RewriteHandles(parentStore.Arena, parentStart, parentCount, ref nodeOps, ref remapVisitor);

        ref readonly var parent = ref parentStore.Arena[new Handle<ParentNode>(0)];
        Assert.Equal(Handle<ParentNode>.None, parent.LeftParent);
        Assert.Equal(0, parent.ChildRef.Index);
        Assert.True(TaggedHandle.IsGlobal(parent.ChildRef.Index));
    }

    [Fact]
    public void Phase2a_GlobalHandles_Untouched()
    {
        var (parentStore, childStore) = CreateStores();

        // Pre-existing global child at index 0
        var globalChild = childStore.Arena.Allocate();
        childStore.Arena[globalChild] = new ChildNode { Value = 99 };
        childStore.RefCounts.EnsureCapacity(1);

        var parentTlbs = new[] { new ThreadLocalBuffer<ParentNode>(0) };
        var parentHandle = parentTlbs[0].Allocate();
        parentTlbs[0][TaggedHandle.DecodeLocalIndex(parentHandle.Index)] = new ParentNode
        {
            LeftParent = Handle<ParentNode>.None,
            ChildRef = globalChild,
        };

        var childRemap = new RemapTable(1);
        var parentRemap = new RemapTable(1);
        var (parentStart, parentCount) = MergePipeline.DrainBuffers(parentStore.Arena, parentStore.RefCounts, parentTlbs, parentRemap);

        var remapVisitor = new RemapVisitor { ParentRemap = parentRemap, ChildRemap = childRemap };
        var nodeOps = new MergeNodeOps { ParentStore = parentStore, ChildStore = childStore };

        MergePipeline.RewriteHandles(parentStore.Arena, parentStart, parentCount, ref nodeOps, ref remapVisitor);

        ref readonly var parent = ref parentStore.Arena[new Handle<ParentNode>(0)];
        Assert.Equal(globalChild, parent.ChildRef);
    }

    // ═══════════════════════════ End-to-end ══════════════════════════════

    /// <summary>
    /// Full 2-type, 2-thread merge pipeline: production, Phase 1, Phase 2a, Phase 2b,
    /// root increment, and DagValidator verification.
    ///
    /// Thread 0: child[0]={Value=10}, parent[0]={ChildRef→child[0]}
    /// Thread 1: child[0]={Value=20}, child[1]={Value=30},
    ///           parent[0]={ChildRef→child[0]}, parent[1]={LeftParent→parent[0], ChildRef→child[1]}
    /// </summary>
    [Fact]
    public void EndToEnd_TwoTypes_TwoThreads()
    {
        var (parentStore, childStore) = CreateStores();
        const int ThreadCount = 2;

        var childTlbs = new ThreadLocalBuffer<ChildNode>[ThreadCount];
        var parentTlbs = new ThreadLocalBuffer<ParentNode>[ThreadCount];
        for (var t = 0; t < ThreadCount; t++)
        {
            childTlbs[t] = new ThreadLocalBuffer<ChildNode>(t);
            parentTlbs[t] = new ThreadLocalBuffer<ParentNode>(t);
        }

        // ── Simulate production phase ────────────────────────────────────

        // Thread 0: one child, one parent referencing it (parent is a root)
        var c0T0 = childTlbs[0].Allocate();
        childTlbs[0][TaggedHandle.DecodeLocalIndex(c0T0.Index)] = new ChildNode { Value = 10 };

        var p0T0 = parentTlbs[0].Allocate(isRoot: true);
        parentTlbs[0][TaggedHandle.DecodeLocalIndex(p0T0.Index)] = new ParentNode
        {
            LeftParent = Handle<ParentNode>.None,
            ChildRef = c0T0,
        };

        // Thread 1: two children, two parents (parent[1] also references parent[0]; parent[1] is a root)
        var c0T1 = childTlbs[1].Allocate();
        childTlbs[1][TaggedHandle.DecodeLocalIndex(c0T1.Index)] = new ChildNode { Value = 20 };

        var c1T1 = childTlbs[1].Allocate();
        childTlbs[1][TaggedHandle.DecodeLocalIndex(c1T1.Index)] = new ChildNode { Value = 30 };

        var p0T1 = parentTlbs[1].Allocate();
        parentTlbs[1][TaggedHandle.DecodeLocalIndex(p0T1.Index)] = new ParentNode
        {
            LeftParent = Handle<ParentNode>.None,
            ChildRef = c0T1,
        };

        var p1T1 = parentTlbs[1].Allocate(isRoot: true);
        parentTlbs[1][TaggedHandle.DecodeLocalIndex(p1T1.Index)] = new ParentNode
        {
            LeftParent = p0T1,
            ChildRef = c1T1,
        };

        // ── Phase 1: Allocate + Copy + Remap ─────────────────────────────

        var childRemap = new RemapTable(ThreadCount);
        var parentRemap = new RemapTable(ThreadCount);

        var (childStart, childCount) = MergePipeline.DrainBuffers(childStore.Arena, childStore.RefCounts, childTlbs, childRemap);
        var (parentStart, parentCount) = MergePipeline.DrainBuffers(parentStore.Arena, parentStore.RefCounts, parentTlbs, parentRemap);

        Assert.Equal(0, childStart);
        Assert.Equal(3, childCount);
        Assert.Equal(0, parentStart);
        Assert.Equal(3, parentCount);

        Assert.Equal(0, childRemap.Resolve(0, 0));
        Assert.Equal(1, childRemap.Resolve(1, 0));
        Assert.Equal(2, childRemap.Resolve(1, 1));
        Assert.Equal(0, parentRemap.Resolve(0, 0));
        Assert.Equal(1, parentRemap.Resolve(1, 0));
        Assert.Equal(2, parentRemap.Resolve(1, 1));

        Assert.Equal(10, childStore.Arena[new Handle<ChildNode>(0)].Value);
        Assert.Equal(20, childStore.Arena[new Handle<ChildNode>(1)].Value);
        Assert.Equal(30, childStore.Arena[new Handle<ChildNode>(2)].Value);

        // ── Phase 2a: Fixup ──────────────────────────────────────────────
        // Barrier: all types finished Phase 1 before any fixup begins.

        var remapVisitor = new RemapVisitor { ParentRemap = parentRemap, ChildRemap = childRemap };
        var nodeOps = new MergeNodeOps { ParentStore = parentStore, ChildStore = childStore };

        MergePipeline.RewriteHandles(parentStore.Arena, parentStart, parentCount, ref nodeOps, ref remapVisitor);
        MergePipeline.RewriteHandles(childStore.Arena, childStart, childCount, ref nodeOps, ref remapVisitor);

        // Verify all handles are now global
        ref readonly var p0 = ref parentStore.Arena[new Handle<ParentNode>(0)];
        Assert.Equal(Handle<ParentNode>.None, p0.LeftParent);
        Assert.Equal(0, p0.ChildRef.Index);
        Assert.True(TaggedHandle.IsGlobal(p0.ChildRef.Index));

        ref readonly var p1 = ref parentStore.Arena[new Handle<ParentNode>(1)];
        Assert.Equal(Handle<ParentNode>.None, p1.LeftParent);
        Assert.Equal(1, p1.ChildRef.Index);
        Assert.True(TaggedHandle.IsGlobal(p1.ChildRef.Index));

        ref readonly var p2 = ref parentStore.Arena[new Handle<ParentNode>(2)];
        Assert.Equal(1, p2.LeftParent.Index);
        Assert.True(TaggedHandle.IsGlobal(p2.LeftParent.Index));
        Assert.Equal(2, p2.ChildRef.Index);
        Assert.True(TaggedHandle.IsGlobal(p2.ChildRef.Index));

        // ── Phase 2b: Refcount ───────────────────────────────────────────

        var refcountVisitor = new RefcountVisitor { ParentStore = parentStore, ChildStore = childStore };

        MergePipeline.IncrementChildRefCounts(parentStore.Arena, parentStart, parentCount, ref nodeOps, ref refcountVisitor);
        MergePipeline.IncrementChildRefCounts(childStore.Arena, childStart, childCount, ref nodeOps, ref refcountVisitor);

        // child[0]: from parent[0] → RC=1
        // child[1]: from parent[1] → RC=1
        // child[2]: from parent[2] → RC=1
        // parent[0]: unreferenced → RC=0
        // parent[1]: from parent[2].LeftParent → RC=1
        // parent[2]: unreferenced → RC=0
        Assert.Equal(1, childStore.RefCounts.GetCount(new Handle<ChildNode>(0)));
        Assert.Equal(1, childStore.RefCounts.GetCount(new Handle<ChildNode>(1)));
        Assert.Equal(1, childStore.RefCounts.GetCount(new Handle<ChildNode>(2)));
        Assert.Equal(0, parentStore.RefCounts.GetCount(new Handle<ParentNode>(0)));
        Assert.Equal(1, parentStore.RefCounts.GetCount(new Handle<ParentNode>(1)));
        Assert.Equal(0, parentStore.RefCounts.GetCount(new Handle<ParentNode>(2)));

        // ── Root collection + remap + increment ──────────────────────────

        var rootList = new UnsafeList<Handle<ParentNode>>();
        MergePipeline.CollectAndRemapRoots(parentTlbs, parentRemap, rootList);
        var roots = rootList.WrittenSpan;

        Assert.Equal(2, roots.Length);
        Assert.Equal(0, roots[0].Index); // p0T0 → global 0
        Assert.Equal(2, roots[1].Index); // p1T1 → global 2

        parentStore.IncrementRoots(roots);

        Assert.Equal(1, parentStore.RefCounts.GetCount(new Handle<ParentNode>(0)));
        Assert.Equal(1, parentStore.RefCounts.GetCount(new Handle<ParentNode>(2)));

        DagValidator.NodeRef[] dagRoots = [new(ParentTypeId, roots[0].Index), new(ParentTypeId, roots[1].Index)];
        DagValidator.AssertValid(dagRoots, new MergeWorldAccessor(parentStore, childStore), strict: true);
    }

    /// <summary>
    /// Verifies that after a full merge-and-root-increment cycle, <see cref="SnapshotSlice{TNode,TNodeOps}"/>
    /// can decrement roots and cascade-free the entire graph, leaving all arenas empty.
    /// </summary>
    [Fact]
    public void EndToEnd_CascadeFree_AfterMerge()
    {
        var (parentStore, childStore) = CreateStores();
        const int ThreadCount = 1;

        var childTlbs = new[] { new ThreadLocalBuffer<ChildNode>(0) };
        var parentTlbs = new[] { new ThreadLocalBuffer<ParentNode>(0) };

        // Single child, single parent referencing it (parent is a root)
        var childHandle = childTlbs[0].Allocate();
        childTlbs[0][TaggedHandle.DecodeLocalIndex(childHandle.Index)] = new ChildNode { Value = 42 };

        var parentHandle = parentTlbs[0].Allocate(isRoot: true);
        parentTlbs[0][TaggedHandle.DecodeLocalIndex(parentHandle.Index)] = new ParentNode
        {
            LeftParent = Handle<ParentNode>.None,
            ChildRef = childHandle,
        };

        // DrainBuffers
        var childRemap = new RemapTable(ThreadCount);
        var parentRemap = new RemapTable(ThreadCount);
        var (childStart, childCount) = MergePipeline.DrainBuffers(childStore.Arena, childStore.RefCounts, childTlbs, childRemap);
        var (parentStart, parentCount) = MergePipeline.DrainBuffers(parentStore.Arena, parentStore.RefCounts, parentTlbs, parentRemap);

        // RewriteHandles
        var remapVisitor = new RemapVisitor { ParentRemap = parentRemap, ChildRemap = childRemap };
        var nodeOps = new MergeNodeOps { ParentStore = parentStore, ChildStore = childStore };
        MergePipeline.RewriteHandles(parentStore.Arena, parentStart, parentCount, ref nodeOps, ref remapVisitor);

        // IncrementChildRefCounts
        var refcountVisitor = new RefcountVisitor { ParentStore = parentStore, ChildStore = childStore };
        MergePipeline.IncrementChildRefCounts(parentStore.Arena, parentStart, parentCount, ref nodeOps, ref refcountVisitor);

        // Root collection + remap + increment
        var rootList = new UnsafeList<Handle<ParentNode>>();
        MergePipeline.CollectAndRemapRoots(parentTlbs, parentRemap, rootList);
        var roots = rootList.WrittenSpan;
        Assert.Equal(1, roots.Length);
        Assert.Equal(0, roots[0].Index);

        parentStore.IncrementRoots(roots);

        Assert.Equal(1, parentStore.RefCounts.GetCount(new Handle<ParentNode>(0)));
        Assert.Equal(1, childStore.RefCounts.GetCount(new Handle<ChildNode>(0)));

        // Decrement roots — should cascade-free the entire graph
        parentStore.DecrementRoots(roots);

        Assert.Equal(0, parentStore.RefCounts.GetCount(new Handle<ParentNode>(0)));
        Assert.Equal(0, childStore.RefCounts.GetCount(new Handle<ChildNode>(0)));
        Assert.Equal(0, parentStore.Arena.GetTestAccessor().Count);
        Assert.Equal(0, childStore.Arena.GetTestAccessor().Count);
    }

    /// <summary>
    /// Verifies the post-Phase2b invariant: root nodes have RC=0 (only structural refcounts from
    /// children have been applied), while non-root nodes referenced by other nodes have RC>0.
    /// After <see cref="NodeStore{TNode,TNodeOps}.IncrementRoots"/>, every root gains RC=1.
    ///
    /// Graph: root(parent[1]) → inner(parent[0]) → leaf(child[0])
    /// </summary>
    [Fact]
    public void RootInvariant_RootsHaveZeroRC_BeforeIncrement()
    {
        var (parentStore, childStore) = CreateStores();
        const int ThreadCount = 1;

        var childTlbs = new[] { new ThreadLocalBuffer<ChildNode>(0) };
        var parentTlbs = new[] { new ThreadLocalBuffer<ParentNode>(0) };

        var leaf = childTlbs[0].Allocate();
        childTlbs[0][TaggedHandle.DecodeLocalIndex(leaf.Index)] = new ChildNode { Value = 1 };

        var inner = parentTlbs[0].Allocate();
        parentTlbs[0][TaggedHandle.DecodeLocalIndex(inner.Index)] = new ParentNode
        {
            LeftParent = Handle<ParentNode>.None,
            ChildRef = leaf,
        };

        var root = parentTlbs[0].Allocate(isRoot: true);
        parentTlbs[0][TaggedHandle.DecodeLocalIndex(root.Index)] = new ParentNode
        {
            LeftParent = inner,
            ChildRef = Handle<ChildNode>.None,
        };

        // Merge: DrainBuffers → RewriteHandles → IncrementChildRefCounts
        var childRemap = new RemapTable(ThreadCount);
        var parentRemap = new RemapTable(ThreadCount);
        MergePipeline.DrainBuffers(childStore.Arena, childStore.RefCounts, childTlbs, childRemap);
        var (parentStart, parentCount) = MergePipeline.DrainBuffers(parentStore.Arena, parentStore.RefCounts, parentTlbs, parentRemap);

        var remapVisitor = new RemapVisitor { ParentRemap = parentRemap, ChildRemap = childRemap };
        var nodeOps = new MergeNodeOps { ParentStore = parentStore, ChildStore = childStore };
        MergePipeline.RewriteHandles(parentStore.Arena, parentStart, parentCount, ref nodeOps, ref remapVisitor);

        var refcountVisitor = new RefcountVisitor { ParentStore = parentStore, ChildStore = childStore };
        MergePipeline.IncrementChildRefCounts(parentStore.Arena, parentStart, parentCount, ref nodeOps, ref refcountVisitor);

        // Post-Phase2b: inner(0) has RC=1 (from root), root(1) has RC=0, leaf(0) has RC=1 (from inner)
        Assert.Equal(1, parentStore.RefCounts.GetCount(new Handle<ParentNode>(0)));
        Assert.Equal(0, parentStore.RefCounts.GetCount(new Handle<ParentNode>(1)));
        Assert.Equal(1, childStore.RefCounts.GetCount(new Handle<ChildNode>(0)));

        // Collect + remap roots, then increment
        var rootList = new UnsafeList<Handle<ParentNode>>();
        MergePipeline.CollectAndRemapRoots(parentTlbs, parentRemap, rootList);
        var roots = rootList.WrittenSpan;
        Assert.Equal(1, roots.Length);
        Assert.Equal(1, roots[0].Index);

        parentStore.IncrementRoots(roots);

        // After increment: root(1) now has RC=1, inner and leaf unchanged
        Assert.Equal(1, parentStore.RefCounts.GetCount(new Handle<ParentNode>(0)));
        Assert.Equal(1, parentStore.RefCounts.GetCount(new Handle<ParentNode>(1)));
        Assert.Equal(1, childStore.RefCounts.GetCount(new Handle<ChildNode>(0)));
    }
}
