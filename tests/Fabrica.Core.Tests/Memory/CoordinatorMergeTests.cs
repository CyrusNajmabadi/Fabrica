using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fabrica.Core.Collections.Unsafe;
using Fabrica.Core.Memory;
using Fabrica.Core.Memory.Nodes;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

/// <summary>
/// Tests the coordinator merge pipeline: Phase 1 (allocate + copy + remap), then fixup and
/// refcount (rewrite local handles to global and increment child refcounts via
/// <see cref="GlobalNodeStore{TNode,TNodeOps}.RewriteAndIncrementRefCounts"/>), root collection,
/// and validation. Uses a 2-type cross-thread scenario with <see cref="ParentNode"/> and
/// <see cref="ChildNode"/>.
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
        internal GlobalNodeStore<ParentNode, MergeNodeOps> ParentStore;
        internal GlobalNodeStore<ChildNode, MergeNodeOps> ChildStore;

        readonly void INodeOps<ParentNode>.EnumerateChildren<TVisitor>(in ParentNode node, ref TVisitor visitor)
        {
            if (node.LeftParent.IsValid) visitor.Visit(node.LeftParent);
            if (node.ChildRef.IsValid) visitor.Visit(node.ChildRef);
        }

        readonly void INodeOps<ChildNode>.EnumerateChildren<TVisitor>(in ChildNode node, ref TVisitor visitor)
        {
        }

        readonly void INodeOps<ParentNode>.IncrementChildRefCounts(in ParentNode node)
        {
            if (node.LeftParent.IsValid) ParentStore.IncrementRefCount(node.LeftParent);
            if (node.ChildRef.IsValid) ChildStore.IncrementRefCount(node.ChildRef);
        }

        readonly void INodeOps<ChildNode>.IncrementChildRefCounts(in ChildNode node)
        {
        }

        readonly void INodeOps<ParentNode>.EnumerateRefChildren<TVisitor>(ref ParentNode node, ref TVisitor visitor)
        {
            if (node.LeftParent != Handle<ParentNode>.None) visitor.VisitRef(ref node.LeftParent);
            if (node.ChildRef != Handle<ChildNode>.None) visitor.VisitRef(ref node.ChildRef);
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

        public readonly void VisitRef<T>(ref Handle<T> handle) where T : struct
        {
            if (typeof(T) == typeof(ParentNode))
            {
                var parentHandle = Unsafe.As<Handle<T>, Handle<ParentNode>>(ref handle);
                parentHandle = ParentStore.RemapHandle(parentHandle);
                handle = Unsafe.As<Handle<ParentNode>, Handle<T>>(ref parentHandle);
            }
            else if (typeof(T) == typeof(ChildNode))
            {
                var childHandle = Unsafe.As<Handle<T>, Handle<ChildNode>>(ref handle);
                childHandle = ChildStore.RemapHandle(childHandle);
                handle = Unsafe.As<Handle<ChildNode>, Handle<T>>(ref childHandle);
            }
        }
    }

    // ── DagValidator accessor ────────────────────────────────────────────

    private const int ParentTypeId = 0;
    private const int ChildTypeId = 1;

    private struct MergeWorldAccessor(
        GlobalNodeStore<ParentNode, MergeNodeOps> parentStore,
        GlobalNodeStore<ChildNode, MergeNodeOps> childStore) : DagValidator.IWorldAccessor
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

    private static (GlobalNodeStore<ParentNode, MergeNodeOps> ParentStore, GlobalNodeStore<ChildNode, MergeNodeOps> ChildStore)
        CreateStores(int workerCount = 1)
    {
        var childStore = new GlobalNodeStore<ChildNode, MergeNodeOps>(workerCount);
        var parentStore = new GlobalNodeStore<ParentNode, MergeNodeOps>(workerCount);

        var ops = new MergeNodeOps { ParentStore = parentStore, ChildStore = childStore };
        childStore.SetNodeOps(ops);
        parentStore.SetNodeOps(ops);

        return (parentStore, childStore);
    }

    // ═══════════════════════════ Phase 1 tests ═══════════════════════════

    [Fact]
    public void Phase1_CopiesData_BuildsRemap()
    {
        var (_, childStore) = CreateStores(2);

        var threadLocalBuffers = childStore.ThreadLocalBuffers;

        var c0 = threadLocalBuffers[0].Allocate();
        threadLocalBuffers[0][c0] = new ChildNode { Value = 10 };

        var c1 = threadLocalBuffers[1].Allocate();
        threadLocalBuffers[1][c1] = new ChildNode { Value = 20 };

        var c2 = threadLocalBuffers[1].Allocate();
        threadLocalBuffers[1][c2] = new ChildNode { Value = 30 };

        var (start, count) = childStore.DrainBuffers();

        Assert.Equal(0, start);
        Assert.Equal(3, count);

        Assert.Equal(10, childStore.Arena[new Handle<ChildNode>(0)].Value);
        Assert.Equal(20, childStore.Arena[new Handle<ChildNode>(1)].Value);
        Assert.Equal(30, childStore.Arena[new Handle<ChildNode>(2)].Value);

        Assert.Equal(0, childStore.GetTestAccessor().Remap.Resolve(0, 0));
        Assert.Equal(1, childStore.GetTestAccessor().Remap.Resolve(1, 0));
        Assert.Equal(2, childStore.GetTestAccessor().Remap.Resolve(1, 1));
    }

    [Fact]
    public void Phase1_EmptyTlb_IsNoOp()
    {
        var (_, childStore) = CreateStores(2);

        var (start, count) = childStore.DrainBuffers();

        Assert.Equal(0, start);
        Assert.Equal(0, count);
        Assert.Equal(0, childStore.Arena.GetTestAccessor().HighWater);
    }

    [Fact]
    public void Phase1_PartiallyEmptyTlbs_OnlyPopulatedThreadsContribute()
    {
        var (_, childStore) = CreateStores(3);

        var threadLocalBuffers = childStore.ThreadLocalBuffers;

        var c0 = threadLocalBuffers[0].Allocate();
        threadLocalBuffers[0][c0] = new ChildNode { Value = 10 };

        // Thread 1 is empty

        var c2 = threadLocalBuffers[2].Allocate();
        threadLocalBuffers[2][c2] = new ChildNode { Value = 30 };

        var (start, count) = childStore.DrainBuffers();

        Assert.Equal(0, start);
        Assert.Equal(2, count);

        Assert.Equal(10, childStore.Arena[new Handle<ChildNode>(0)].Value);
        Assert.Equal(30, childStore.Arena[new Handle<ChildNode>(1)].Value);

        Assert.Equal(0, childStore.GetTestAccessor().Remap.Resolve(0, 0));
        Assert.Equal(0, childStore.GetTestAccessor().Remap.Count(1));
        Assert.Equal(1, childStore.GetTestAccessor().Remap.Resolve(2, 0));
    }

    // ═══════════════════════════ Phase 2a tests ══════════════════════════

    [Fact]
    public void Phase2a_RemapsLocalHandlesToGlobal()
    {
        var (parentStore, childStore) = CreateStores(1);

        var childThreadLocalBuffers = childStore.ThreadLocalBuffers;
        var parentThreadLocalBuffers = parentStore.ThreadLocalBuffers;

        var childHandle = childThreadLocalBuffers[0].Allocate();
        childThreadLocalBuffers[0][childHandle] = new ChildNode { Value = 42 };

        var parentHandle = parentThreadLocalBuffers[0].Allocate();
        parentThreadLocalBuffers[0][parentHandle] = new ParentNode
        {
            LeftParent = Handle<ParentNode>.None,
            ChildRef = childHandle,
        };

        childStore.DrainBuffers();
        parentStore.DrainBuffers();

        parentStore.RewriteAndIncrementRefCounts();

        ref readonly var parent = ref parentStore.Arena[new Handle<ParentNode>(0)];
        Assert.Equal(Handle<ParentNode>.None, parent.LeftParent);
        Assert.Equal(0, parent.ChildRef.Index);
        Assert.True(TaggedHandle.IsGlobal(parent.ChildRef.Index));
    }

    [Fact]
    public void Phase2a_GlobalHandles_Untouched()
    {
        var (parentStore, childStore) = CreateStores(1);

        // Pre-existing global child at index 0
        var globalChild = childStore.Arena.Allocate();
        childStore.Arena[globalChild] = new ChildNode { Value = 99 };
        childStore.RefCounts.EnsureCapacity(1);

        var parentThreadLocalBuffers = parentStore.ThreadLocalBuffers;
        var parentHandle = parentThreadLocalBuffers[0].Allocate();
        parentThreadLocalBuffers[0][parentHandle] = new ParentNode
        {
            LeftParent = Handle<ParentNode>.None,
            ChildRef = globalChild,
        };

        parentStore.DrainBuffers();

        parentStore.RewriteAndIncrementRefCounts();

        ref readonly var parent = ref parentStore.Arena[new Handle<ParentNode>(0)];
        Assert.Equal(globalChild, parent.ChildRef);
    }

    // ═══════════════════════════ End-to-end ══════════════════════════════

    /// <summary>
    /// Full 2-type, 2-thread merge pipeline: production, Phase 1, rewrite + refcount,
    /// root increment, and DagValidator verification.
    ///
    /// Thread 0: child[0]={Value=10}, parent[0]={ChildRef→child[0]}
    /// Thread 1: child[0]={Value=20}, child[1]={Value=30},
    ///           parent[0]={ChildRef→child[0]}, parent[1]={LeftParent→parent[0], ChildRef→child[1]}
    /// </summary>
    [Fact]
    public void EndToEnd_TwoTypes_TwoThreads()
    {
        const int ThreadCount = 2;
        var (parentStore, childStore) = CreateStores(ThreadCount);

        var childThreadLocalBuffers = childStore.ThreadLocalBuffers;
        var parentThreadLocalBuffers = parentStore.ThreadLocalBuffers;

        // ── Simulate production phase ────────────────────────────────────

        // Thread 0: one child, one parent referencing it (parent is a root)
        var c0T0 = childThreadLocalBuffers[0].Allocate();
        childThreadLocalBuffers[0][c0T0] = new ChildNode { Value = 10 };

        var p0T0 = parentThreadLocalBuffers[0].Allocate(isRoot: true);
        parentThreadLocalBuffers[0][p0T0] = new ParentNode
        {
            LeftParent = Handle<ParentNode>.None,
            ChildRef = c0T0,
        };

        // Thread 1: two children, two parents (parent[1] also references parent[0]; parent[1] is a root)
        var c0T1 = childThreadLocalBuffers[1].Allocate();
        childThreadLocalBuffers[1][c0T1] = new ChildNode { Value = 20 };

        var c1T1 = childThreadLocalBuffers[1].Allocate();
        childThreadLocalBuffers[1][c1T1] = new ChildNode { Value = 30 };

        var p0T1 = parentThreadLocalBuffers[1].Allocate();
        parentThreadLocalBuffers[1][p0T1] = new ParentNode
        {
            LeftParent = Handle<ParentNode>.None,
            ChildRef = c0T1,
        };

        var p1T1 = parentThreadLocalBuffers[1].Allocate(isRoot: true);
        parentThreadLocalBuffers[1][p1T1] = new ParentNode
        {
            LeftParent = p0T1,
            ChildRef = c1T1,
        };

        // ── Phase 1: Allocate + Copy + Remap ─────────────────────────────

        var (childStart, childCount) = childStore.DrainBuffers();
        var (parentStart, parentCount) = parentStore.DrainBuffers();

        Assert.Equal(0, childStart);
        Assert.Equal(3, childCount);
        Assert.Equal(0, parentStart);
        Assert.Equal(3, parentCount);

        Assert.Equal(0, childStore.GetTestAccessor().Remap.Resolve(0, 0));
        Assert.Equal(1, childStore.GetTestAccessor().Remap.Resolve(1, 0));
        Assert.Equal(2, childStore.GetTestAccessor().Remap.Resolve(1, 1));
        Assert.Equal(0, parentStore.GetTestAccessor().Remap.Resolve(0, 0));
        Assert.Equal(1, parentStore.GetTestAccessor().Remap.Resolve(1, 0));
        Assert.Equal(2, parentStore.GetTestAccessor().Remap.Resolve(1, 1));

        Assert.Equal(10, childStore.Arena[new Handle<ChildNode>(0)].Value);
        Assert.Equal(20, childStore.Arena[new Handle<ChildNode>(1)].Value);
        Assert.Equal(30, childStore.Arena[new Handle<ChildNode>(2)].Value);

        // ── Rewrite + refcount ───────────────────────────────────────────
        // Barrier: all types finished Phase 1 before fixup begins.

        parentStore.RewriteAndIncrementRefCounts();
        childStore.RewriteAndIncrementRefCounts();

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
        parentStore.GetTestAccessor().CollectAndRemapRoots(rootList);
        var roots = rootList.WrittenSpan;

        Assert.Equal(2, roots.Length);
        Assert.Equal(0, roots[0].Index); // p0T0 → global 0
        Assert.Equal(2, roots[1].Index); // p1T1 → global 2

        parentStore.GetTestAccessor().IncrementRoots(roots);

        Assert.Equal(1, parentStore.RefCounts.GetCount(new Handle<ParentNode>(0)));
        Assert.Equal(1, parentStore.RefCounts.GetCount(new Handle<ParentNode>(2)));

        DagValidator.NodeRef[] dagRoots = [new(ParentTypeId, roots[0].Index), new(ParentTypeId, roots[1].Index)];
        DagValidator.AssertValid(dagRoots, new MergeWorldAccessor(parentStore, childStore), strict: true);
    }

    /// <summary>
    /// Verifies that after a full merge-and-root-increment cycle, <see cref="GlobalNodeStore{TNode,TNodeOps}.ReleaseSnapshotSlice"/>
    /// cascade-frees the entire graph, leaving all arenas empty.
    /// </summary>
    [Fact]
    public void EndToEnd_CascadeFree_AfterMerge()
    {
        var (parentStore, childStore) = CreateStores(1);

        var childThreadLocalBuffers = childStore.ThreadLocalBuffers;
        var parentThreadLocalBuffers = parentStore.ThreadLocalBuffers;

        // Single child, single parent referencing it (parent is a root)
        var childHandle = childThreadLocalBuffers[0].Allocate();
        childThreadLocalBuffers[0][childHandle] = new ChildNode { Value = 42 };

        var parentHandle = parentThreadLocalBuffers[0].Allocate(isRoot: true);
        parentThreadLocalBuffers[0][parentHandle] = new ParentNode
        {
            LeftParent = Handle<ParentNode>.None,
            ChildRef = childHandle,
        };

        // DrainBuffers
        childStore.DrainBuffers();
        parentStore.DrainBuffers();

        parentStore.RewriteAndIncrementRefCounts();

        // Root collection + remap + increment
        var rootList = new UnsafeList<Handle<ParentNode>>();
        parentStore.GetTestAccessor().CollectAndRemapRoots(rootList);
        var roots = rootList.WrittenSpan;
        Assert.Equal(1, roots.Length);
        Assert.Equal(0, roots[0].Index);

        parentStore.GetTestAccessor().IncrementRoots(roots);

        Assert.Equal(1, parentStore.RefCounts.GetCount(new Handle<ParentNode>(0)));
        Assert.Equal(1, childStore.RefCounts.GetCount(new Handle<ChildNode>(0)));

        // ReleaseSnapshotSlice — should cascade-free the entire graph
        parentStore.GetTestAccessor().DecrementRoots(roots);

        Assert.Equal(0, parentStore.RefCounts.GetCount(new Handle<ParentNode>(0)));
        Assert.Equal(0, childStore.RefCounts.GetCount(new Handle<ChildNode>(0)));
        Assert.Equal(0, parentStore.Arena.GetTestAccessor().Count);
        Assert.Equal(0, childStore.Arena.GetTestAccessor().Count);
    }

    /// <summary>
    /// Verifies the post-Phase2b invariant: root nodes have RC=0 (only structural refcounts from
    /// children have been applied), while non-root nodes referenced by other nodes have RC>0.
    /// After <see cref="GlobalNodeStore{TNode,TNodeOps}.GetTestAccessor"/>.IncrementRoots, every root gains RC=1.
    ///
    /// Graph: root(parent[1]) → inner(parent[0]) → leaf(child[0])
    /// </summary>
    [Fact]
    public void RootInvariant_RootsHaveZeroRC_BeforeIncrement()
    {
        var (parentStore, childStore) = CreateStores(1);

        var childThreadLocalBuffers = childStore.ThreadLocalBuffers;
        var parentThreadLocalBuffers = parentStore.ThreadLocalBuffers;

        var leaf = childThreadLocalBuffers[0].Allocate();
        childThreadLocalBuffers[0][leaf] = new ChildNode { Value = 1 };

        var inner = parentThreadLocalBuffers[0].Allocate();
        parentThreadLocalBuffers[0][inner] = new ParentNode
        {
            LeftParent = Handle<ParentNode>.None,
            ChildRef = leaf,
        };

        var root = parentThreadLocalBuffers[0].Allocate(isRoot: true);
        parentThreadLocalBuffers[0][root] = new ParentNode
        {
            LeftParent = inner,
            ChildRef = Handle<ChildNode>.None,
        };

        // Merge: DrainBuffers → RewriteAndIncrementRefCounts
        childStore.DrainBuffers();
        parentStore.DrainBuffers();

        parentStore.RewriteAndIncrementRefCounts();

        // After structural refcount pass: inner(0) has RC=1 (from root), root(1) has RC=0, leaf(0) has RC=1 (from inner)
        Assert.Equal(1, parentStore.RefCounts.GetCount(new Handle<ParentNode>(0)));
        Assert.Equal(0, parentStore.RefCounts.GetCount(new Handle<ParentNode>(1)));
        Assert.Equal(1, childStore.RefCounts.GetCount(new Handle<ChildNode>(0)));

        // Collect + remap roots, then increment
        var rootList = new UnsafeList<Handle<ParentNode>>();
        parentStore.GetTestAccessor().CollectAndRemapRoots(rootList);
        var roots = rootList.WrittenSpan;
        Assert.Equal(1, roots.Length);
        Assert.Equal(1, roots[0].Index);

        parentStore.GetTestAccessor().IncrementRoots(roots);

        // After increment: root(1) now has RC=1, inner and leaf unchanged
        Assert.Equal(1, parentStore.RefCounts.GetCount(new Handle<ParentNode>(0)));
        Assert.Equal(1, parentStore.RefCounts.GetCount(new Handle<ParentNode>(1)));
        Assert.Equal(1, childStore.RefCounts.GetCount(new Handle<ChildNode>(0)));
    }
}
