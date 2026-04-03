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
        public int LeftParent { get; set; }
        public int RightParent { get; set; }
        public int ChildRef { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChildNode
    {
        public int LeftChild { get; set; }
        public int RightChild { get; set; }
        public int Value { get; set; }
    }

    // ── Handlers ─────────────────────────────────────────────────────────

    private struct ChildHandler(UnsafeSlabArena<ChildNode> arena) : RefCountTable.IRefCountHandler
    {
        public readonly void OnFreed(int index, RefCountTable table)
        {
            ref readonly var node = ref arena[index];
            if (node.LeftChild >= 0) table.Decrement(node.LeftChild, this);
            if (node.RightChild >= 0) table.Decrement(node.RightChild, this);
            arena.Free(index);
        }
    }

    private struct ParentHandler(
        UnsafeSlabArena<ParentNode> parentArena,
        NodeStore<ChildNode, ChildHandler> childStore) : RefCountTable.IRefCountHandler
    {
        public readonly void OnFreed(int index, RefCountTable table)
        {
            ref readonly var node = ref parentArena[index];
            if (node.LeftParent >= 0) table.Decrement(node.LeftParent, this);
            if (node.RightParent >= 0) table.Decrement(node.RightParent, this);
            if (node.ChildRef >= 0)
                childStore.RefCounts.Decrement(node.ChildRef, childStore.GetTestAccessor().Handler);
            parentArena.Free(index);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static (NodeStore<ParentNode, ParentHandler> parentStore, NodeStore<ChildNode, ChildHandler> childStore)
        CreateStores()
    {
        var childArena = new UnsafeSlabArena<ChildNode>();
        var childRefCounts = new RefCountTable();
        var childHandler = new ChildHandler(childArena);
        var childStore = new NodeStore<ChildNode, ChildHandler>(childArena, childRefCounts, childHandler);

        var parentArena = new UnsafeSlabArena<ParentNode>();
        var parentRefCounts = new RefCountTable();
        var parentHandler = new ParentHandler(parentArena, childStore);
        var parentStore = new NodeStore<ParentNode, ParentHandler>(parentArena, parentRefCounts, parentHandler);

        return (parentStore, childStore);
    }

    private static int AllocChild(NodeStore<ChildNode, ChildHandler> store, int left, int right, int value)
    {
        var index = store.Arena.Allocate();
        store.RefCounts.EnsureCapacity(index + 1);
        store.Arena[index] = new ChildNode { LeftChild = left, RightChild = right, Value = value };
        if (left >= 0) store.RefCounts.Increment(left);
        if (right >= 0) store.RefCounts.Increment(right);
        return index;
    }

    private static int AllocParent(
        NodeStore<ParentNode, ParentHandler> parentStore,
        NodeStore<ChildNode, ChildHandler> childStore,
        int leftParent, int rightParent, int childRef)
    {
        var index = parentStore.Arena.Allocate();
        parentStore.RefCounts.EnsureCapacity(index + 1);
        parentStore.Arena[index] = new ParentNode { LeftParent = leftParent, RightParent = rightParent, ChildRef = childRef };
        if (leftParent >= 0) parentStore.RefCounts.Increment(leftParent);
        if (rightParent >= 0) parentStore.RefCounts.Increment(rightParent);
        if (childRef >= 0) childStore.RefCounts.Increment(childRef);
        return index;
    }

    // ── Tests ────────────────────────────────────────────────────────────

    [Fact]
    public void CrossType_ReleasingParent_FreesChildWhenUnreferenced()
    {
        var (parentStore, childStore) = CreateStores();

        // Child tree: childRoot → (childA, childB)
        var childA = AllocChild(childStore, -1, -1, 1);
        var childB = AllocChild(childStore, -1, -1, 2);
        var childRoot = AllocChild(childStore, childA, childB, 0);

        // Parent node references childRoot
        var parentRoot = AllocParent(parentStore, childStore, -1, -1, childRoot);

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

        var childLeaf = AllocChild(childStore, -1, -1, 42);

        // Two parent nodes both reference the same child
        var parent1 = AllocParent(parentStore, childStore, -1, -1, childLeaf);
        var parent2 = AllocParent(parentStore, childStore, -1, -1, childLeaf);

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

        var childNode = AllocChild(childStore, -1, -1, 99);
        var parentNode = AllocParent(parentStore, childStore, -1, -1, childNode);

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

        var childA = AllocChild(childStore, -1, -1, 1);
        var childB = AllocChild(childStore, -1, -1, 2);
        var childRoot = AllocChild(childStore, childA, childB, 0);

        var parentNode = AllocParent(parentStore, childStore, -1, -1, childRoot);

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
        var cA = AllocChild(childStore, -1, -1, 1);
        var cB = AllocChild(childStore, -1, -1, 2);
        var cRoot = AllocChild(childStore, cA, cB, 0);

        var pLeaf = AllocParent(parentStore, childStore, -1, -1, -1);
        var pRoot = AllocParent(parentStore, childStore, pLeaf, -1, cRoot);

        // Extra standalone child root (not referenced by any parent)
        var cStandalone = AllocChild(childStore, -1, -1, 99);

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

        var cLeaf = AllocChild(childStore, -1, -1, 1);
        var pNode = AllocParent(parentStore, childStore, -1, -1, cLeaf);

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
        var cA = AllocChild(childStore, -1, -1, 1);
        var cB = AllocChild(childStore, -1, -1, 2);
        var shared = AllocChild(childStore, cA, cB, 10);

        var cC = AllocChild(childStore, -1, -1, 3);
        var exclusive = AllocChild(childStore, cC, -1, 20);

        var pRoot1 = AllocParent(parentStore, childStore, -1, -1, shared);
        var pRoot2 = AllocParent(parentStore, childStore, -1, -1, shared);
        var pRoot3 = AllocParent(parentStore, childStore, -1, -1, exclusive);

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
        int first, int second, int third)
    {
        var (parentStore, childStore) = CreateStores();

        // Shared child subtree across all three snapshots
        var cA = AllocChild(childStore, -1, -1, 1);
        var cB = AllocChild(childStore, -1, -1, 2);
        var sharedChild = AllocChild(childStore, cA, cB, 0);

        var parentRoots = new int[3];
        var parentSlices = new SnapshotSlice<ParentNode, ParentHandler>[3];
        var childSlices = new SnapshotSlice<ChildNode, ChildHandler>[3];

        for (var i = 0; i < 3; i++)
        {
            // Each snapshot has its own parent root pointing at the shared child
            parentRoots[i] = AllocParent(parentStore, childStore, -1, -1, sharedChild);

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
        var cLeaf1 = AllocChild(childStore, -1, -1, 1);
        var cLeaf2 = AllocChild(childStore, -1, -1, 2);
        var cMid = AllocChild(childStore, cLeaf1, cLeaf2, 10);
        var cLeaf3 = AllocChild(childStore, -1, -1, 3);
        var cRoot = AllocChild(childStore, cMid, cLeaf3, 0);

        // Parent tree: pRoot → (pMid → pLeaf), pRoot.ChildRef → cRoot
        //              pMid.ChildRef → cMid (points into middle of child tree)
        var pLeaf = AllocParent(parentStore, childStore, -1, -1, -1);
        var pMid = AllocParent(parentStore, childStore, pLeaf, -1, cMid);
        var pRoot = AllocParent(parentStore, childStore, pMid, -1, cRoot);

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
        var cLeaf = AllocChild(childStore, -1, -1, 1);
        var cMid = AllocChild(childStore, cLeaf, -1, 10);
        var cRoot = AllocChild(childStore, cMid, -1, 0);

        var pRoot = AllocParent(parentStore, childStore, -1, -1, cMid);

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
}
