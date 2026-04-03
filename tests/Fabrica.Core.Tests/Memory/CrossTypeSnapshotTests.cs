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
}
