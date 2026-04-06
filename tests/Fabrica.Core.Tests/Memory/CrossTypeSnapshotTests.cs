using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

/// <summary>
/// Tests cross-type DAGs: type A nodes referencing type B nodes stored in a different <see cref="NodeStore{TNode,THandler}"/>.
/// Validates that cascade-free correctly crosses store boundaries.
/// </summary>
public class CrossTypeSnapshotTests
{
    // ── Node types ───────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct ParentNode
    {
        public Handle<ParentNode> LeftParent;
        public Handle<ParentNode> RightParent;
        public Handle<ChildNode> ChildRef;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChildNode
    {
        public Handle<ChildNode> LeftChild;
        public Handle<ChildNode> RightChild;
        public int Value;
    }

    // ── Handlers ─────────────────────────────────────────────────────────

    private struct ChildHandler(UnsafeSlabArena<ChildNode> arena, ChildChildEnumerator enumerator) : RefCountTable<ChildNode>.IRefCountHandler
    {
        public readonly void OnFreed(Handle<ChildNode> handle, RefCountTable<ChildNode> table)
        {
            ref var node = ref arena[handle];
            var visitor = new RefCountTable<ChildNode>.DecrementNodeRefCountVisitor<ChildHandler>(table, this);
            enumerator.EnumerateChildren(ref node, ref visitor);
            arena.Free(handle);
        }
    }

    /// <summary>
    /// Cross-type decrement: dispatches to the correct <see cref="RefCountTable{T}"/> based on
    /// the child's type. The <c>typeof</c> checks are JIT constants — dead branches are eliminated.
    /// </summary>
    private struct ParentDecrementNodeVisitor(
        RefCountTable<ParentNode> parentTable,
        ParentHandler parentHandler,
        RefCountTable<ChildNode> childTable,
        ChildHandler childHandler) : INodeVisitor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Visit<TChild>(ref Handle<TChild> child) where TChild : struct
        {
            if (typeof(TChild) == typeof(ParentNode))
                this.DecrementParent(Unsafe.As<Handle<TChild>, Handle<ParentNode>>(ref child));
            else if (typeof(TChild) == typeof(ChildNode))
                this.DecrementChild(Unsafe.As<Handle<TChild>, Handle<ChildNode>>(ref child));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly void DecrementParent(Handle<ParentNode> child)
        {
            Debug.Assert(child.IsValid);
            parentTable.Decrement(child, parentHandler);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly void DecrementChild(Handle<ChildNode> child)
        {
            Debug.Assert(child.IsValid);
            childTable.Decrement(child, childHandler);
        }
    }

    private struct ParentHandler(
        UnsafeSlabArena<ParentNode> parentArena,
        NodeStore<ChildNode, ChildHandler> childStore,
        ParentChildEnumerator enumerator) : RefCountTable<ParentNode>.IRefCountHandler
    {
        public readonly void OnFreed(Handle<ParentNode> handle, RefCountTable<ParentNode> table)
        {
            ref var node = ref parentArena[handle];
            var visitor = new ParentDecrementNodeVisitor(table, this, childStore.RefCounts, childStore.GetTestAccessor().Handler);
            enumerator.EnumerateChildren(ref node, ref visitor);
            parentArena.Free(handle);
        }
    }

    // ── Child enumerators (all children, including cross-type) ─────────

    private struct ParentChildEnumerator : INodeChildEnumerator<ParentNode>
    {
        public readonly void EnumerateChildren<TAction>(ref ParentNode node, ref TAction visitor)
            where TAction : struct, INodeVisitor
        {
            if (node.LeftParent.Index != -1) visitor.Visit(ref node.LeftParent);
            if (node.RightParent.Index != -1) visitor.Visit(ref node.RightParent);
            if (node.ChildRef.Index != -1) visitor.Visit(ref node.ChildRef);
        }

        public readonly void EnumerateChildren<TAction, TContext>(ref ParentNode node, in TContext context, ref TAction visitor)
            where TAction : struct, INodeVisitor<TContext>
        {
            if (node.LeftParent.Index != -1) visitor.Visit(ref node.LeftParent, in context);
            if (node.RightParent.Index != -1) visitor.Visit(ref node.RightParent, in context);
            if (node.ChildRef.Index != -1) visitor.Visit(ref node.ChildRef, in context);
        }
    }

    private struct ChildChildEnumerator : INodeChildEnumerator<ChildNode>
    {
        public readonly void EnumerateChildren<TAction>(ref ChildNode node, ref TAction visitor)
            where TAction : struct, INodeVisitor
        {
            if (node.LeftChild.Index != -1) visitor.Visit(ref node.LeftChild);
            if (node.RightChild.Index != -1) visitor.Visit(ref node.RightChild);
        }

        public readonly void EnumerateChildren<TAction, TContext>(ref ChildNode node, in TContext context, ref TAction visitor)
            where TAction : struct, INodeVisitor<TContext>
        {
            if (node.LeftChild.Index != -1) visitor.Visit(ref node.LeftChild, in context);
            if (node.RightChild.Index != -1) visitor.Visit(ref node.RightChild, in context);
        }
    }

    // ── Cross-store world accessor ──────────────────────────────────────

    private const int ParentTypeId = 0;
    private const int ChildTypeId = 1;

    private struct CrossStoreAccessor(
        NodeStore<ParentNode, ParentHandler> parentStore,
        NodeStore<ChildNode, ChildHandler> childStore) : DagValidator.IWorldAccessor
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
                if (node.RightParent.IsValid) children.Add(new DagValidator.NodeRef(ParentTypeId, node.RightParent.Index));
                if (node.ChildRef.IsValid) children.Add(new DagValidator.NodeRef(ChildTypeId, node.ChildRef.Index));
            }
            else if (typeId == ChildTypeId)
            {
                ref readonly var node = ref childStore.Arena[new Handle<ChildNode>(index)];
                if (node.LeftChild.IsValid) children.Add(new DagValidator.NodeRef(ChildTypeId, node.LeftChild.Index));
                if (node.RightChild.IsValid) children.Add(new DagValidator.NodeRef(ChildTypeId, node.RightChild.Index));
            }
        }
    }

    private static void AssertCrossStoreValid(
        NodeStore<ParentNode, ParentHandler> parentStore,
        NodeStore<ChildNode, ChildHandler> childStore,
        ReadOnlySpan<Handle<ParentNode>> parentRoots,
        ReadOnlySpan<Handle<ChildNode>> childRoots,
        bool strict = true)
    {
        var allRoots = new DagValidator.NodeRef[parentRoots.Length + childRoots.Length];
        for (var i = 0; i < parentRoots.Length; i++)
            allRoots[i] = new DagValidator.NodeRef(ParentTypeId, parentRoots[i].Index);
        for (var i = 0; i < childRoots.Length; i++)
            allRoots[parentRoots.Length + i] = new DagValidator.NodeRef(ChildTypeId, childRoots[i].Index);

        DagValidator.AssertValid(allRoots, new CrossStoreAccessor(parentStore, childStore), strict);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static (NodeStore<ParentNode, ParentHandler> parentStore, NodeStore<ChildNode, ChildHandler> childStore)
        CreateStores()
    {
        var childArena = new UnsafeSlabArena<ChildNode>();
        var childRefCounts = new RefCountTable<ChildNode>();
        var childEnumerator = new ChildChildEnumerator();
        var childHandler = new ChildHandler(childArena, childEnumerator);
        var childStore = new NodeStore<ChildNode, ChildHandler>(childArena, childRefCounts, childHandler);
        childStore.EnableValidation(childEnumerator);

        var parentArena = new UnsafeSlabArena<ParentNode>();
        var parentRefCounts = new RefCountTable<ParentNode>();
        var parentEnumerator = new ParentChildEnumerator();
        var parentHandler = new ParentHandler(parentArena, childStore, parentEnumerator);
        var parentStore = new NodeStore<ParentNode, ParentHandler>(parentArena, parentRefCounts, parentHandler);
        parentStore.EnableValidation(parentEnumerator);

        return (parentStore, childStore);
    }

    private static Handle<ChildNode> AllocChild(
        NodeStore<ChildNode, ChildHandler> store,
        Handle<ChildNode> left,
        Handle<ChildNode> right,
        int value)
    {
        var handle = store.Arena.Allocate();
        store.RefCounts.EnsureCapacity(handle.Index + 1);
        store.Arena[handle] = new ChildNode { LeftChild = left, RightChild = right, Value = value };
        if (left.IsValid)
            store.RefCounts.Increment(left);
        if (right.IsValid)
            store.RefCounts.Increment(right);
        return handle;
    }

    private static Handle<ParentNode> AllocParent(
        NodeStore<ParentNode, ParentHandler> parentStore,
        NodeStore<ChildNode, ChildHandler> childStore,
        Handle<ParentNode> leftParent,
        Handle<ParentNode> rightParent,
        Handle<ChildNode> childRef)
    {
        var handle = parentStore.Arena.Allocate();
        parentStore.RefCounts.EnsureCapacity(handle.Index + 1);
        parentStore.Arena[handle] = new ParentNode
        {
            LeftParent = leftParent,
            RightParent = rightParent,
            ChildRef = childRef,
        };
        if (leftParent.IsValid)
            parentStore.RefCounts.Increment(leftParent);
        if (rightParent.IsValid)
            parentStore.RefCounts.Increment(rightParent);
        if (childRef.IsValid)
            childStore.RefCounts.Increment(childRef);
        return handle;
    }

    // ── Tests ────────────────────────────────────────────────────────────

    [Fact]
    public void CrossType_ReleasingParent_FreesChildWhenUnreferenced()
    {
        var (parentStore, childStore) = CreateStores();

        // Child tree: childRoot → (childA, childB)
        var childA = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 1);
        var childB = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 2);
        var childRoot = AllocChild(childStore, childA, childB, 0);

        // Parent node references childRoot
        var parentRoot = AllocParent(
            parentStore,
            childStore,
            Handle<ParentNode>.None,
            Handle<ParentNode>.None,
            childRoot);

        var parentSlice = new SnapshotSlice<ParentNode, ParentHandler>(parentStore);
        parentSlice.AddRoot(parentRoot);
        parentSlice.IncrementRootRefCounts();

        Assert.Equal(1, parentStore.RefCounts.GetCount(parentRoot));
        Assert.Equal(1, childStore.RefCounts.GetCount(childRoot));
        Assert.Equal(1, childStore.RefCounts.GetCount(childA));
        Assert.Equal(1, childStore.RefCounts.GetCount(childB));

        parentSlice.DecrementRootRefCounts();

        Assert.Equal(0, parentStore.RefCounts.GetCount(parentRoot));
        Assert.Equal(0, childStore.RefCounts.GetCount(childRoot));
        Assert.Equal(0, childStore.RefCounts.GetCount(childA));
        Assert.Equal(0, childStore.RefCounts.GetCount(childB));
    }

    [Fact]
    public void CrossType_SharedChild_SurvivesPartialRelease()
    {
        var (parentStore, childStore) = CreateStores();

        var childLeaf = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 42);

        // Two parent nodes both reference the same child
        var parent1 = AllocParent(
            parentStore,
            childStore,
            Handle<ParentNode>.None,
            Handle<ParentNode>.None,
            childLeaf);
        var parent2 = AllocParent(
            parentStore,
            childStore,
            Handle<ParentNode>.None,
            Handle<ParentNode>.None,
            childLeaf);

        var slice1 = new SnapshotSlice<ParentNode, ParentHandler>(parentStore);
        slice1.AddRoot(parent1);
        slice1.IncrementRootRefCounts();

        var slice2 = new SnapshotSlice<ParentNode, ParentHandler>(parentStore);
        slice2.AddRoot(parent2);
        slice2.IncrementRootRefCounts();

        Assert.Equal(2, childStore.RefCounts.GetCount(childLeaf));

        slice1.DecrementRootRefCounts();
        Assert.Equal(1, childStore.RefCounts.GetCount(childLeaf));

        slice2.DecrementRootRefCounts();
        Assert.Equal(0, childStore.RefCounts.GetCount(childLeaf));
    }

    [Fact]
    public void CrossType_ChildAlsoHasDirectRoot_SurvivesParentRelease()
    {
        var (parentStore, childStore) = CreateStores();

        var childNode = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 99);
        var parentNode = AllocParent(
            parentStore,
            childStore,
            Handle<ParentNode>.None,
            Handle<ParentNode>.None,
            childNode);

        // Parent holds childNode via cross-type reference
        var parentSlice = new SnapshotSlice<ParentNode, ParentHandler>(parentStore);
        parentSlice.AddRoot(parentNode);
        parentSlice.IncrementRootRefCounts();

        // Child is ALSO a direct root in a child snapshot
        var childSlice = new SnapshotSlice<ChildNode, ChildHandler>(childStore);
        childSlice.AddRoot(childNode);
        childSlice.IncrementRootRefCounts();

        Assert.Equal(2, childStore.RefCounts.GetCount(childNode));

        parentSlice.DecrementRootRefCounts();
        Assert.Equal(1, childStore.RefCounts.GetCount(childNode));

        childSlice.DecrementRootRefCounts();
        Assert.Equal(0, childStore.RefCounts.GetCount(childNode));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CrossType_BothReleaseOrders_Correct(bool releaseParentFirst)
    {
        var (parentStore, childStore) = CreateStores();

        var childA = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 1);
        var childB = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 2);
        var childRoot = AllocChild(childStore, childA, childB, 0);

        var parentNode = AllocParent(
            parentStore,
            childStore,
            Handle<ParentNode>.None,
            Handle<ParentNode>.None,
            childRoot);

        var parentSlice = new SnapshotSlice<ParentNode, ParentHandler>(parentStore);
        parentSlice.AddRoot(parentNode);
        parentSlice.IncrementRootRefCounts();

        var childSlice = new SnapshotSlice<ChildNode, ChildHandler>(childStore);
        childSlice.AddRoot(childRoot);
        childSlice.IncrementRootRefCounts();

        Assert.Equal(2, childStore.RefCounts.GetCount(childRoot));

        if (releaseParentFirst)
        {
            parentSlice.DecrementRootRefCounts();
            Assert.Equal(1, childStore.RefCounts.GetCount(childRoot));
            Assert.Equal(1, childStore.RefCounts.GetCount(childA));

            childSlice.DecrementRootRefCounts();
        }
        else
        {
            childSlice.DecrementRootRefCounts();
            Assert.Equal(1, childStore.RefCounts.GetCount(childRoot));
            Assert.Equal(1, childStore.RefCounts.GetCount(childA));

            parentSlice.DecrementRootRefCounts();
        }

        Assert.Equal(0, childStore.RefCounts.GetCount(childRoot));
        Assert.Equal(0, childStore.RefCounts.GetCount(childA));
        Assert.Equal(0, childStore.RefCounts.GetCount(childB));
        Assert.Equal(0, parentStore.RefCounts.GetCount(parentNode));
    }

    // ── Composite snapshot: both slices with multiple roots ──────────────

    [Fact]
    public void CompositeSnapshot_BothSlices_AllFreedOnRelease()
    {
        var (parentStore, childStore) = CreateStores();

        // Child tree:  cRoot → (cA, cB)
        // Parent tree: pRoot → pLeaf, pRoot.ChildRef → cRoot
        var cA = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 1);
        var cB = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 2);
        var cRoot = AllocChild(childStore, cA, cB, 0);

        var pLeaf = AllocParent(
            parentStore,
            childStore,
            Handle<ParentNode>.None,
            Handle<ParentNode>.None,
            Handle<ChildNode>.None);
        var pRoot = AllocParent(parentStore, childStore, pLeaf, Handle<ParentNode>.None, cRoot);

        // Extra standalone child root (not referenced by any parent)
        var cStandalone = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 99);

        // Composite snapshot: parent slice has pRoot, child slice has cRoot + cStandalone
        var parentSlice = new SnapshotSlice<ParentNode, ParentHandler>(parentStore);
        parentSlice.AddRoot(pRoot);
        parentSlice.IncrementRootRefCounts();

        var childSlice = new SnapshotSlice<ChildNode, ChildHandler>(childStore);
        childSlice.AddRoot(cRoot);
        childSlice.AddRoot(cStandalone);
        childSlice.IncrementRootRefCounts();

        // cRoot has refcount 2: one from pRoot's ChildRef, one from childSlice root
        Assert.Equal(2, childStore.RefCounts.GetCount(cRoot));
        Assert.Equal(1, childStore.RefCounts.GetCount(cStandalone));
        Assert.Equal(1, parentStore.RefCounts.GetCount(pRoot));

        // Release both slices (simulating releasing the composite snapshot)
        parentSlice.DecrementRootRefCounts();
        childSlice.DecrementRootRefCounts();

        Assert.Equal(0, parentStore.RefCounts.GetCount(pRoot));
        Assert.Equal(0, parentStore.RefCounts.GetCount(pLeaf));
        Assert.Equal(0, childStore.RefCounts.GetCount(cRoot));
        Assert.Equal(0, childStore.RefCounts.GetCount(cA));
        Assert.Equal(0, childStore.RefCounts.GetCount(cB));
        Assert.Equal(0, childStore.RefCounts.GetCount(cStandalone));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompositeSnapshot_SliceReleaseOrder_DoesNotMatter(bool releaseParentSliceFirst)
    {
        var (parentStore, childStore) = CreateStores();

        var cLeaf = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 1);
        var pNode = AllocParent(
            parentStore,
            childStore,
            Handle<ParentNode>.None,
            Handle<ParentNode>.None,
            cLeaf);

        var parentSlice = new SnapshotSlice<ParentNode, ParentHandler>(parentStore);
        parentSlice.AddRoot(pNode);
        parentSlice.IncrementRootRefCounts();

        var childSlice = new SnapshotSlice<ChildNode, ChildHandler>(childStore);
        childSlice.AddRoot(cLeaf);
        childSlice.IncrementRootRefCounts();

        Assert.Equal(2, childStore.RefCounts.GetCount(cLeaf));

        if (releaseParentSliceFirst)
        {
            parentSlice.DecrementRootRefCounts();
            Assert.Equal(1, childStore.RefCounts.GetCount(cLeaf));
            childSlice.DecrementRootRefCounts();
        }
        else
        {
            childSlice.DecrementRootRefCounts();
            Assert.Equal(1, childStore.RefCounts.GetCount(cLeaf));
            parentSlice.DecrementRootRefCounts();
        }

        Assert.Equal(0, childStore.RefCounts.GetCount(cLeaf));
        Assert.Equal(0, parentStore.RefCounts.GetCount(pNode));
    }

    [Fact]
    public void CompositeSnapshot_MultipleParentRoots_PointAtOverlappingChildSubtrees()
    {
        var (parentStore, childStore) = CreateStores();

        //  Child store:
        //    shared → (cA, cB)
        //    exclusive → (cC, -)
        //
        //  Parent store:
        //    pRoot1 → pRoot1 children: none, ChildRef → shared
        //    pRoot2 → pRoot2 children: none, ChildRef → shared  (overlapping)
        //    pRoot3 → pRoot3 children: none, ChildRef → exclusive
        var cA = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 1);
        var cB = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 2);
        var shared = AllocChild(childStore, cA, cB, 10);

        var cC = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 3);
        var exclusive = AllocChild(childStore, cC, Handle<ChildNode>.None, 20);

        var pRoot1 = AllocParent(
            parentStore,
            childStore,
            Handle<ParentNode>.None,
            Handle<ParentNode>.None,
            shared);
        var pRoot2 = AllocParent(
            parentStore,
            childStore,
            Handle<ParentNode>.None,
            Handle<ParentNode>.None,
            shared);
        var pRoot3 = AllocParent(
            parentStore,
            childStore,
            Handle<ParentNode>.None,
            Handle<ParentNode>.None,
            exclusive);

        var parentSlice = new SnapshotSlice<ParentNode, ParentHandler>(parentStore);
        parentSlice.AddRoot(pRoot1);
        parentSlice.AddRoot(pRoot2);
        parentSlice.AddRoot(pRoot3);
        parentSlice.IncrementRootRefCounts();

        Assert.Equal(2, childStore.RefCounts.GetCount(shared));
        Assert.Equal(1, childStore.RefCounts.GetCount(exclusive));
        Assert.Equal(1, childStore.RefCounts.GetCount(cA));
        Assert.Equal(1, childStore.RefCounts.GetCount(cB));
        Assert.Equal(1, childStore.RefCounts.GetCount(cC));

        parentSlice.DecrementRootRefCounts();

        Assert.Equal(0, childStore.RefCounts.GetCount(shared));
        Assert.Equal(0, childStore.RefCounts.GetCount(exclusive));
        Assert.Equal(0, childStore.RefCounts.GetCount(cA));
        Assert.Equal(0, childStore.RefCounts.GetCount(cB));
        Assert.Equal(0, childStore.RefCounts.GetCount(cC));
    }

    // ── Multiple composite snapshots with structural sharing ─────────────

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(0, 2, 1)]
    [InlineData(1, 0, 2)]
    [InlineData(1, 2, 0)]
    [InlineData(2, 0, 1)]
    [InlineData(2, 1, 0)]
    public void ThreeCompositeSnapshots_SharedChildSubtree_AllReleaseOrders(
        int first,
        int second,
        int third)
    {
        var (parentStore, childStore) = CreateStores();

        // Shared child subtree across all three snapshots
        var cA = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 1);
        var cB = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 2);
        var sharedChild = AllocChild(childStore, cA, cB, 0);

        var parentRoots = new Handle<ParentNode>[3];
        var parentSlices = new SnapshotSlice<ParentNode, ParentHandler>[3];
        var childSlices = new SnapshotSlice<ChildNode, ChildHandler>[3];

        for (var i = 0; i < 3; i++)
        {
            // Each snapshot has its own parent root pointing at the shared child
            parentRoots[i] = AllocParent(
                parentStore,
                childStore,
                Handle<ParentNode>.None,
                Handle<ParentNode>.None,
                sharedChild);

            parentSlices[i] = new SnapshotSlice<ParentNode, ParentHandler>(parentStore);
            parentSlices[i].AddRoot(parentRoots[i]);
            parentSlices[i].IncrementRootRefCounts();

            // Each snapshot also directly roots the shared child
            childSlices[i] = new SnapshotSlice<ChildNode, ChildHandler>(childStore);
            childSlices[i].AddRoot(sharedChild);
            childSlices[i].IncrementRootRefCounts();
        }

        // sharedChild: 3 from parent ChildRef + 3 from child slice roots = 6
        Assert.Equal(6, childStore.RefCounts.GetCount(sharedChild));

        var order = new[] { first, second, third };
        for (var step = 0; step < 3; step++)
        {
            var idx = order[step];
            parentSlices[idx].DecrementRootRefCounts();
            childSlices[idx].DecrementRootRefCounts();

            Assert.Equal(0, parentStore.RefCounts.GetCount(parentRoots[idx]));

            var remaining = 2 - step;
            // Each remaining snapshot contributes 2 refs to sharedChild (1 parent + 1 child root)
            Assert.Equal(remaining * 2, childStore.RefCounts.GetCount(sharedChild));
        }

        Assert.Equal(0, childStore.RefCounts.GetCount(sharedChild));
        Assert.Equal(0, childStore.RefCounts.GetCount(cA));
        Assert.Equal(0, childStore.RefCounts.GetCount(cB));
    }

    [Fact]
    public void CompositeSnapshot_DeepCrossTypeDAG_CascadesCorrectly()
    {
        var (parentStore, childStore) = CreateStores();

        // Deep child tree: cRoot → (cMid → (cLeaf1, cLeaf2), cLeaf3)
        var cLeaf1 = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 1);
        var cLeaf2 = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 2);
        var cMid = AllocChild(childStore, cLeaf1, cLeaf2, 10);
        var cLeaf3 = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 3);
        var cRoot = AllocChild(childStore, cMid, cLeaf3, 0);

        // Parent tree: pRoot → (pMid → pLeaf), pRoot.ChildRef → cRoot
        //              pMid.ChildRef → cMid (points into middle of child tree)
        var pLeaf = AllocParent(
            parentStore,
            childStore,
            Handle<ParentNode>.None,
            Handle<ParentNode>.None,
            Handle<ChildNode>.None);
        var pMid = AllocParent(parentStore, childStore, pLeaf, Handle<ParentNode>.None, cMid);
        var pRoot = AllocParent(parentStore, childStore, pMid, Handle<ParentNode>.None, cRoot);

        var parentSlice = new SnapshotSlice<ParentNode, ParentHandler>(parentStore);
        parentSlice.AddRoot(pRoot);
        parentSlice.IncrementRootRefCounts();

        // cRoot: 1 (from pRoot.ChildRef)
        // cMid: 1 (child of cRoot) + 1 (from pMid.ChildRef) = 2
        Assert.Equal(1, childStore.RefCounts.GetCount(cRoot));
        Assert.Equal(2, childStore.RefCounts.GetCount(cMid));
        Assert.Equal(1, childStore.RefCounts.GetCount(cLeaf1));
        Assert.Equal(1, childStore.RefCounts.GetCount(cLeaf2));
        Assert.Equal(1, childStore.RefCounts.GetCount(cLeaf3));

        parentSlice.DecrementRootRefCounts();

        Assert.Equal(0, parentStore.RefCounts.GetCount(pRoot));
        Assert.Equal(0, parentStore.RefCounts.GetCount(pMid));
        Assert.Equal(0, parentStore.RefCounts.GetCount(pLeaf));
        Assert.Equal(0, childStore.RefCounts.GetCount(cRoot));
        Assert.Equal(0, childStore.RefCounts.GetCount(cMid));
        Assert.Equal(0, childStore.RefCounts.GetCount(cLeaf1));
        Assert.Equal(0, childStore.RefCounts.GetCount(cLeaf2));
        Assert.Equal(0, childStore.RefCounts.GetCount(cLeaf3));
    }

    [Fact]
    public void CompositeSnapshot_ParentPointsAtChildMidLevel_ChildSliceRootsLeaf()
    {
        var (parentStore, childStore) = CreateStores();

        // Child tree:  cRoot → (cMid → (cLeaf, -), -)
        // Parent points at cMid (not the root)
        // Child slice roots cLeaf directly
        var cLeaf = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 1);
        var cMid = AllocChild(childStore, cLeaf, Handle<ChildNode>.None, 10);
        var cRoot = AllocChild(childStore, cMid, Handle<ChildNode>.None, 0);

        var pRoot = AllocParent(
            parentStore,
            childStore,
            Handle<ParentNode>.None,
            Handle<ParentNode>.None,
            cMid);

        var parentSlice = new SnapshotSlice<ParentNode, ParentHandler>(parentStore);
        parentSlice.AddRoot(pRoot);
        parentSlice.IncrementRootRefCounts();

        var childSlice = new SnapshotSlice<ChildNode, ChildHandler>(childStore);
        childSlice.AddRoot(cRoot);
        childSlice.AddRoot(cLeaf);
        childSlice.IncrementRootRefCounts();

        // cMid: 1 (child of cRoot) + 1 (from pRoot.ChildRef) = 2
        // cLeaf: 1 (child of cMid) + 1 (child slice root) = 2
        Assert.Equal(1, childStore.RefCounts.GetCount(cRoot));
        Assert.Equal(2, childStore.RefCounts.GetCount(cMid));
        Assert.Equal(2, childStore.RefCounts.GetCount(cLeaf));

        // Release parent first — cMid drops to 1, cLeaf stays at 2
        parentSlice.DecrementRootRefCounts();
        Assert.Equal(1, childStore.RefCounts.GetCount(cMid));
        Assert.Equal(2, childStore.RefCounts.GetCount(cLeaf));

        // Release child slice — everything cascades to 0
        childSlice.DecrementRootRefCounts();
        Assert.Equal(0, childStore.RefCounts.GetCount(cRoot));
        Assert.Equal(0, childStore.RefCounts.GetCount(cMid));
        Assert.Equal(0, childStore.RefCounts.GetCount(cLeaf));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Cross-store DAG validation tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void CrossStoreValidation_SimpleTree_Valid()
    {
        var (parentStore, childStore) = CreateStores();

        var childLeaf = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 1);
        var parentRoot = AllocParent(parentStore, childStore,
            Handle<ParentNode>.None, Handle<ParentNode>.None, childLeaf);

        parentStore.RefCounts.Increment(parentRoot);

        AssertCrossStoreValid(parentStore, childStore, [parentRoot], []);
    }

    [Fact]
    public void CrossStoreValidation_DeepCrossTypeDAG_Valid()
    {
        var (parentStore, childStore) = CreateStores();

        var cLeaf1 = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 1);
        var cLeaf2 = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 2);
        var cRoot = AllocChild(childStore, cLeaf1, cLeaf2, 0);

        var pLeaf = AllocParent(parentStore, childStore,
            Handle<ParentNode>.None, Handle<ParentNode>.None, Handle<ChildNode>.None);
        var pRoot = AllocParent(parentStore, childStore, pLeaf, Handle<ParentNode>.None, cRoot);

        parentStore.RefCounts.Increment(pRoot);

        AssertCrossStoreValid(parentStore, childStore, [pRoot], []);
    }

    [Fact]
    public void CrossStoreValidation_SharedChild_BothSnapshotRoots_Valid()
    {
        var (parentStore, childStore) = CreateStores();

        var childLeaf = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 42);
        var parentRoot = AllocParent(parentStore, childStore,
            Handle<ParentNode>.None, Handle<ParentNode>.None, childLeaf);

        parentStore.RefCounts.Increment(parentRoot);
        childStore.RefCounts.Increment(childLeaf);

        // Relaxed: childLeaf is both a root and reachable through parentRoot's ChildRef
        AssertCrossStoreValid(parentStore, childStore, [parentRoot], [childLeaf], strict: false);
    }

    [Fact]
    public void CrossStoreValidation_DetectsRefcountMismatch()
    {
        var (parentStore, childStore) = CreateStores();

        var childLeaf = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 1);
        var parentRoot = AllocParent(parentStore, childStore,
            Handle<ParentNode>.None, Handle<ParentNode>.None, childLeaf);

        parentStore.RefCounts.Increment(parentRoot);
        // Artificially inflate childLeaf's refcount — should be 1 (from parent) but we make it 2
        childStore.RefCounts.Increment(childLeaf);

        var accessor = new CrossStoreAccessor(parentStore, childStore);
        var roots = new[] { new DagValidator.NodeRef(ParentTypeId, parentRoot.Index) };
        var issues = DagValidator.Validate(roots, accessor);

        Assert.Contains(issues, i => i.Contains($"index={childLeaf.Index}") && i.Contains("expected refcount 1, actual 2"));
    }

    [Fact]
    public void CrossStoreValidation_DetectsOrphanInChildStore()
    {
        var (parentStore, childStore) = CreateStores();

        var childLeaf = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 1);
        var parentRoot = AllocParent(parentStore, childStore,
            Handle<ParentNode>.None, Handle<ParentNode>.None, childLeaf);

        parentStore.RefCounts.Increment(parentRoot);

        // Allocate an orphan child node with refcount > 0 that isn't reachable from any root
        var orphan = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 99);
        childStore.RefCounts.Increment(orphan);

        var accessor = new CrossStoreAccessor(parentStore, childStore);
        var roots = new[] { new DagValidator.NodeRef(ParentTypeId, parentRoot.Index) };
        var issues = DagValidator.Validate(roots, accessor);

        Assert.Contains(issues, i => i.Contains($"index={orphan.Index}") && i.Contains("reachable=False"));
    }

    [Fact]
    public void CrossStoreValidation_EmptyWorld_Valid()
    {
        var (parentStore, childStore) = CreateStores();

        AssertCrossStoreValid(parentStore, childStore, [], []);
    }

    [Fact]
    public void CrossStoreValidation_AfterFullRelease_Empty()
    {
        var (parentStore, childStore) = CreateStores();

        var cLeaf = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 1);
        var pRoot = AllocParent(parentStore, childStore,
            Handle<ParentNode>.None, Handle<ParentNode>.None, cLeaf);

        var parentSlice = new SnapshotSlice<ParentNode, ParentHandler>(parentStore);
        parentSlice.AddRoot(pRoot);
        parentSlice.IncrementRootRefCounts();

        // Valid while alive
        AssertCrossStoreValid(parentStore, childStore, [pRoot], []);

        // Release — everything freed
        parentSlice.DecrementRootRefCounts();

        // Valid after full release (empty world)
        AssertCrossStoreValid(parentStore, childStore, [], []);
    }

    [Fact]
    public void CrossStoreValidation_MultipleParentRootsWithSharedChild_Valid()
    {
        var (parentStore, childStore) = CreateStores();

        var sharedChild = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 1);

        var p1 = AllocParent(parentStore, childStore,
            Handle<ParentNode>.None, Handle<ParentNode>.None, sharedChild);
        var p2 = AllocParent(parentStore, childStore,
            Handle<ParentNode>.None, Handle<ParentNode>.None, sharedChild);

        parentStore.RefCounts.Increment(p1);
        parentStore.RefCounts.Increment(p2);

        AssertCrossStoreValid(parentStore, childStore, [p1, p2], []);
    }

    [Fact]
    public void CrossStoreValidation_ParentAndChildRoots_OverlappingSubtree_Valid()
    {
        var (parentStore, childStore) = CreateStores();

        var cA = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 1);
        var cB = AllocChild(childStore, Handle<ChildNode>.None, Handle<ChildNode>.None, 2);
        var cRoot = AllocChild(childStore, cA, cB, 0);

        var pRoot = AllocParent(parentStore, childStore,
            Handle<ParentNode>.None, Handle<ParentNode>.None, cRoot);

        parentStore.RefCounts.Increment(pRoot);
        childStore.RefCounts.Increment(cRoot);

        // cRoot has refcount 2: one from parent's ChildRef, one from being a child root
        // Relaxed: cRoot is both a root and reachable through pRoot's ChildRef
        AssertCrossStoreValid(parentStore, childStore, [pRoot], [cRoot], strict: false);
    }
}
