using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fabrica.Core.Memory;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

/// <summary>
/// Tests cross-type DAGs: type A nodes referencing type B nodes stored in a different <see cref="NodeStore{TNode,TNodeOps}"/>.
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

    // ── Node ops — single struct implementing INodeOps for both types ───
    //
    // Demonstrates that one struct can implement INodeOps<T> for multiple T values.
    // The EnumerateChildren methods are per-type (explicit interface implementations),
    // while Visit<T> is shared (from INodeVisitor) and dispatches via typeof checks.

    private struct CrossTypeNodeOps : INodeOps<ParentNode>, INodeOps<ChildNode>
    {
        internal NodeStore<ParentNode, CrossTypeNodeOps> ParentStore;
        internal NodeStore<ChildNode, CrossTypeNodeOps> ChildStore;

        readonly void INodeOps<ParentNode>.EnumerateChildren<TVisitor>(in ParentNode node, ref TVisitor visitor)
        {
            if (node.LeftParent.IsValid) visitor.Visit(node.LeftParent);
            if (node.RightParent.IsValid) visitor.Visit(node.RightParent);
            if (node.ChildRef.IsValid) visitor.Visit(node.ChildRef);
        }

        readonly void INodeOps<ChildNode>.EnumerateChildren<TVisitor>(in ChildNode node, ref TVisitor visitor)
        {
            if (node.LeftChild.IsValid) visitor.Visit(node.LeftChild);
            if (node.RightChild.IsValid) visitor.Visit(node.RightChild);
        }

        public readonly void Visit<T>(Handle<T> handle)
            where T : struct
        {
            if (typeof(T) == typeof(ParentNode))
            {
                ParentStore.DecrementRefCount(Unsafe.As<Handle<T>, Handle<ParentNode>>(ref handle));
            }
            else if (typeof(T) == typeof(ChildNode))
            {
                ChildStore.DecrementRefCount(Unsafe.As<Handle<T>, Handle<ChildNode>>(ref handle));
            }
        }
    }

    // ── Cross-store world accessor ──────────────────────────────────────

    private const int ParentTypeId = 0;
    private const int ChildTypeId = 1;

    private struct CrossStoreAccessor(
        NodeStore<ParentNode, CrossTypeNodeOps> parentStore,
        NodeStore<ChildNode, CrossTypeNodeOps> childStore) : DagValidator.IWorldAccessor
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
        NodeStore<ParentNode, CrossTypeNodeOps> parentStore,
        NodeStore<ChildNode, CrossTypeNodeOps> childStore,
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

    private static (NodeStore<ParentNode, CrossTypeNodeOps> parentStore, NodeStore<ChildNode, CrossTypeNodeOps> childStore)
        CreateStores()
    {
        var childArena = new UnsafeSlabArena<ChildNode>();
        var childRefCounts = new RefCountTable<ChildNode>();
        var childStore = new NodeStore<ChildNode, CrossTypeNodeOps>(childArena, childRefCounts, default);

        var parentArena = new UnsafeSlabArena<ParentNode>();
        var parentRefCounts = new RefCountTable<ParentNode>();
        var parentStore = new NodeStore<ParentNode, CrossTypeNodeOps>(parentArena, parentRefCounts, default);

        var ops = new CrossTypeNodeOps { ParentStore = parentStore, ChildStore = childStore };
        childStore.SetNodeOps(ops);
        parentStore.SetNodeOps(ops);
        childStore.EnableValidation();
        parentStore.EnableValidation();

        return (parentStore, childStore);
    }

    private static Handle<ChildNode> AllocChild(
        NodeStore<ChildNode, CrossTypeNodeOps> store,
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
        NodeStore<ParentNode, CrossTypeNodeOps> parentStore,
        NodeStore<ChildNode, CrossTypeNodeOps> childStore,
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

        var parentSlice = new SnapshotSlice<ParentNode, CrossTypeNodeOps>(parentStore, new UnsafeList<Handle<ParentNode>>());
        parentSlice.AddRoot(parentRoot);
        parentSlice.IncrementRootRefCounts();

        Assert.Equal(1, parentStore.RefCounts.GetCount(parentRoot));
        Assert.Equal(1, childStore.RefCounts.GetCount(childRoot));
        Assert.Equal(1, childStore.RefCounts.GetCount(childA));
        Assert.Equal(1, childStore.RefCounts.GetCount(childB));

        parentStore.DecrementRoots(parentSlice.Roots);

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

        var slice1 = new SnapshotSlice<ParentNode, CrossTypeNodeOps>(parentStore, new UnsafeList<Handle<ParentNode>>());
        slice1.AddRoot(parent1);
        slice1.IncrementRootRefCounts();

        var slice2 = new SnapshotSlice<ParentNode, CrossTypeNodeOps>(parentStore, new UnsafeList<Handle<ParentNode>>());
        slice2.AddRoot(parent2);
        slice2.IncrementRootRefCounts();

        Assert.Equal(2, childStore.RefCounts.GetCount(childLeaf));

        parentStore.DecrementRoots(slice1.Roots);
        Assert.Equal(1, childStore.RefCounts.GetCount(childLeaf));

        parentStore.DecrementRoots(slice2.Roots);
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
        var parentSlice = new SnapshotSlice<ParentNode, CrossTypeNodeOps>(parentStore, new UnsafeList<Handle<ParentNode>>());
        parentSlice.AddRoot(parentNode);
        parentSlice.IncrementRootRefCounts();

        // Child is ALSO a direct root in a child snapshot
        var childSlice = new SnapshotSlice<ChildNode, CrossTypeNodeOps>(childStore, new UnsafeList<Handle<ChildNode>>());
        childSlice.AddRoot(childNode);
        childSlice.IncrementRootRefCounts();

        Assert.Equal(2, childStore.RefCounts.GetCount(childNode));

        parentStore.DecrementRoots(parentSlice.Roots);
        Assert.Equal(1, childStore.RefCounts.GetCount(childNode));

        childStore.DecrementRoots(childSlice.Roots);
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

        var parentSlice = new SnapshotSlice<ParentNode, CrossTypeNodeOps>(parentStore, new UnsafeList<Handle<ParentNode>>());
        parentSlice.AddRoot(parentNode);
        parentSlice.IncrementRootRefCounts();

        var childSlice = new SnapshotSlice<ChildNode, CrossTypeNodeOps>(childStore, new UnsafeList<Handle<ChildNode>>());
        childSlice.AddRoot(childRoot);
        childSlice.IncrementRootRefCounts();

        Assert.Equal(2, childStore.RefCounts.GetCount(childRoot));

        if (releaseParentFirst)
        {
            parentStore.DecrementRoots(parentSlice.Roots);
            Assert.Equal(1, childStore.RefCounts.GetCount(childRoot));
            Assert.Equal(1, childStore.RefCounts.GetCount(childA));

            childStore.DecrementRoots(childSlice.Roots);
        }
        else
        {
            childStore.DecrementRoots(childSlice.Roots);
            Assert.Equal(1, childStore.RefCounts.GetCount(childRoot));
            Assert.Equal(1, childStore.RefCounts.GetCount(childA));

            parentStore.DecrementRoots(parentSlice.Roots);
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
        var parentSlice = new SnapshotSlice<ParentNode, CrossTypeNodeOps>(parentStore, new UnsafeList<Handle<ParentNode>>());
        parentSlice.AddRoot(pRoot);
        parentSlice.IncrementRootRefCounts();

        var childSlice = new SnapshotSlice<ChildNode, CrossTypeNodeOps>(childStore, new UnsafeList<Handle<ChildNode>>());
        childSlice.AddRoot(cRoot);
        childSlice.AddRoot(cStandalone);
        childSlice.IncrementRootRefCounts();

        // cRoot has refcount 2: one from pRoot's ChildRef, one from childSlice root
        Assert.Equal(2, childStore.RefCounts.GetCount(cRoot));
        Assert.Equal(1, childStore.RefCounts.GetCount(cStandalone));
        Assert.Equal(1, parentStore.RefCounts.GetCount(pRoot));

        // Release both slices (simulating releasing the composite snapshot)
        parentStore.DecrementRoots(parentSlice.Roots);
        childStore.DecrementRoots(childSlice.Roots);

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

        var parentSlice = new SnapshotSlice<ParentNode, CrossTypeNodeOps>(parentStore, new UnsafeList<Handle<ParentNode>>());
        parentSlice.AddRoot(pNode);
        parentSlice.IncrementRootRefCounts();

        var childSlice = new SnapshotSlice<ChildNode, CrossTypeNodeOps>(childStore, new UnsafeList<Handle<ChildNode>>());
        childSlice.AddRoot(cLeaf);
        childSlice.IncrementRootRefCounts();

        Assert.Equal(2, childStore.RefCounts.GetCount(cLeaf));

        if (releaseParentSliceFirst)
        {
            parentStore.DecrementRoots(parentSlice.Roots);
            Assert.Equal(1, childStore.RefCounts.GetCount(cLeaf));
            childStore.DecrementRoots(childSlice.Roots);
        }
        else
        {
            childStore.DecrementRoots(childSlice.Roots);
            Assert.Equal(1, childStore.RefCounts.GetCount(cLeaf));
            parentStore.DecrementRoots(parentSlice.Roots);
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

        var parentSlice = new SnapshotSlice<ParentNode, CrossTypeNodeOps>(parentStore, new UnsafeList<Handle<ParentNode>>());
        parentSlice.AddRoot(pRoot1);
        parentSlice.AddRoot(pRoot2);
        parentSlice.AddRoot(pRoot3);
        parentSlice.IncrementRootRefCounts();

        Assert.Equal(2, childStore.RefCounts.GetCount(shared));
        Assert.Equal(1, childStore.RefCounts.GetCount(exclusive));
        Assert.Equal(1, childStore.RefCounts.GetCount(cA));
        Assert.Equal(1, childStore.RefCounts.GetCount(cB));
        Assert.Equal(1, childStore.RefCounts.GetCount(cC));

        parentStore.DecrementRoots(parentSlice.Roots);

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
        var parentSlices = new SnapshotSlice<ParentNode, CrossTypeNodeOps>[3];
        var childSlices = new SnapshotSlice<ChildNode, CrossTypeNodeOps>[3];

        for (var i = 0; i < 3; i++)
        {
            // Each snapshot has its own parent root pointing at the shared child
            parentRoots[i] = AllocParent(
                parentStore,
                childStore,
                Handle<ParentNode>.None,
                Handle<ParentNode>.None,
                sharedChild);

            parentSlices[i] = new SnapshotSlice<ParentNode, CrossTypeNodeOps>(parentStore, new UnsafeList<Handle<ParentNode>>());
            parentSlices[i].AddRoot(parentRoots[i]);
            parentSlices[i].IncrementRootRefCounts();

            // Each snapshot also directly roots the shared child
            childSlices[i] = new SnapshotSlice<ChildNode, CrossTypeNodeOps>(childStore, new UnsafeList<Handle<ChildNode>>());
            childSlices[i].AddRoot(sharedChild);
            childSlices[i].IncrementRootRefCounts();
        }

        // sharedChild: 3 from parent ChildRef + 3 from child slice roots = 6
        Assert.Equal(6, childStore.RefCounts.GetCount(sharedChild));

        var order = new[] { first, second, third };
        for (var step = 0; step < 3; step++)
        {
            var idx = order[step];
            parentStore.DecrementRoots(parentSlices[idx].Roots);
            childStore.DecrementRoots(childSlices[idx].Roots);

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

        var parentSlice = new SnapshotSlice<ParentNode, CrossTypeNodeOps>(parentStore, new UnsafeList<Handle<ParentNode>>());
        parentSlice.AddRoot(pRoot);
        parentSlice.IncrementRootRefCounts();

        // cRoot: 1 (from pRoot.ChildRef)
        // cMid: 1 (child of cRoot) + 1 (from pMid.ChildRef) = 2
        Assert.Equal(1, childStore.RefCounts.GetCount(cRoot));
        Assert.Equal(2, childStore.RefCounts.GetCount(cMid));
        Assert.Equal(1, childStore.RefCounts.GetCount(cLeaf1));
        Assert.Equal(1, childStore.RefCounts.GetCount(cLeaf2));
        Assert.Equal(1, childStore.RefCounts.GetCount(cLeaf3));

        parentStore.DecrementRoots(parentSlice.Roots);

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

        var parentSlice = new SnapshotSlice<ParentNode, CrossTypeNodeOps>(parentStore, new UnsafeList<Handle<ParentNode>>());
        parentSlice.AddRoot(pRoot);
        parentSlice.IncrementRootRefCounts();

        var childSlice = new SnapshotSlice<ChildNode, CrossTypeNodeOps>(childStore, new UnsafeList<Handle<ChildNode>>());
        childSlice.AddRoot(cRoot);
        childSlice.AddRoot(cLeaf);
        childSlice.IncrementRootRefCounts();

        // cMid: 1 (child of cRoot) + 1 (from pRoot.ChildRef) = 2
        // cLeaf: 1 (child of cMid) + 1 (child slice root) = 2
        Assert.Equal(1, childStore.RefCounts.GetCount(cRoot));
        Assert.Equal(2, childStore.RefCounts.GetCount(cMid));
        Assert.Equal(2, childStore.RefCounts.GetCount(cLeaf));

        // Release parent first — cMid drops to 1, cLeaf stays at 2
        parentStore.DecrementRoots(parentSlice.Roots);
        Assert.Equal(1, childStore.RefCounts.GetCount(cMid));
        Assert.Equal(2, childStore.RefCounts.GetCount(cLeaf));

        // Release child slice — everything cascades to 0
        childStore.DecrementRoots(childSlice.Roots);
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

        var parentSlice = new SnapshotSlice<ParentNode, CrossTypeNodeOps>(parentStore, new UnsafeList<Handle<ParentNode>>());
        parentSlice.AddRoot(pRoot);
        parentSlice.IncrementRootRefCounts();

        // Valid while alive
        AssertCrossStoreValid(parentStore, childStore, [pRoot], []);

        // Release — everything freed
        parentStore.DecrementRoots(parentSlice.Roots);

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
