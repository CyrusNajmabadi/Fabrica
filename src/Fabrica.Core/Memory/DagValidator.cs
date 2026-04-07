using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Fabrica.Core.Memory;

/// <summary>
/// Validates structural invariants of arena-backed DAGs, including heterogeneous graphs that span
/// multiple <see cref="GlobalNodeStore{TNode,TNodeOps}"/> instances of different node types.
///
/// CROSS-STORE VALIDATION
///   The primary API takes an <see cref="IWorldAccessor"/> that provides type-erased access to all
///   stores in the world. Each node is identified by a <see cref="NodeRef"/> (typeId + index). The
///   DFS walks across store boundaries, detecting cross-store cycles and verifying refcounts globally.
///
/// SINGLE-STORE CONVENIENCE
///   Overloads that take a single <see cref="GlobalNodeStore{TNode,TNodeOps}"/> and an
///   <see cref="INodeOps{TNode}"/> wrap into a <see cref="SingleStoreAccessor{TNode,TNodeOps}"/>
///   internally. Cross-type children from the enumerator are ignored (they contribute external
///   refcounts that this store can't verify).
///
/// TWO MODES
///   <b>Strict</b> (default): checks exact refcount match, reachability, root purity, empty-roots.
///   <b>Relaxed</b>: checks <c>actual &gt;= expected</c>. Tolerates extra refcounts from external
///   references not modeled in the provided roots.
///
/// INVARIANTS CHECKED (both modes)
///   1. Acyclicity — the graph is a DAG, not a cyclic graph.
///   2. Refcount floor — every reachable node's stored refcount is at least the computed count.
///   3. No dangling references — children of reachable nodes are within valid index ranges.
///
/// ADDITIONAL INVARIANTS (strict mode only)
///   4. Refcount accuracy — every node's stored refcount exactly matches the computed count.
///   5. Reachability — every node with refcount &gt; 0 is reachable from at least one root.
///   6. Root purity — no root is a child of another reachable node.
///
/// PORTABILITY
///   Pure computation over arrays/indices. No GC-specific features.
/// </summary>
internal static class DagValidator
{
    /// <summary>Type-erased node identity: a (typeId, index) pair that uniquely identifies a node across stores.</summary>
    internal readonly record struct NodeRef(int TypeId, int Index);

    /// <summary>
    /// Struct callback that provides the validator type-erased access to a heterogeneous DAG world.
    /// Implementations dispatch on <c>typeId</c> to access the correct <see cref="GlobalNodeStore{TNode,THandler}"/>.
    /// </summary>
    internal interface IWorldAccessor
    {
        /// <summary>Number of distinct node types in this world.</summary>
        int TypeCount { get; }

        /// <summary>High-water mark (exclusive) for the given type — the maximum valid index + 1.</summary>
        int HighWater(int typeId);

        /// <summary>Returns the stored refcount for the node at (typeId, index).</summary>
        int GetRefCount(int typeId, int index);

        /// <summary>
        /// Appends the children of the node at (typeId, index) to <paramref name="children"/>.
        /// Children may be of any type — each child's <see cref="NodeRef.TypeId"/> identifies its store.
        /// Invalid children (index &lt; 0) must be filtered out by the implementation.
        /// </summary>
        void GetChildren(int typeId, int index, List<NodeRef> children);
    }

    // ── Cross-store API ─────────────────────────────────────────────────

    /// <summary>
    /// Validates the entire heterogeneous DAG and throws <see cref="DagValidationException"/> if any
    /// invariants are violated.
    /// </summary>
    internal static void AssertValid<TAccessor>(
        ReadOnlySpan<NodeRef> roots,
        TAccessor accessor,
        bool strict = true)
        where TAccessor : struct, IWorldAccessor
    {
        var issues = Validate(roots, accessor, strict);
        if (issues.Count > 0)
            throw new DagValidationException(issues);
    }

    /// <summary>
    /// Validates the entire heterogeneous DAG and returns a list of violation descriptions.
    /// An empty list means the DAG is well-formed.
    /// </summary>
    internal static List<string> Validate<TAccessor>(
        ReadOnlySpan<NodeRef> roots,
        TAccessor accessor,
        bool strict = true)
        where TAccessor : struct, IWorldAccessor
    {
        var issues = new List<string>();
        var typeCount = accessor.TypeCount;

        // Per-type arrays for expected refcounts and DFS coloring
        var expectedRefCount = new int[typeCount][];
        var color = new int[typeCount][];  // 0=white, 1=gray, 2=black
        var isChildOfSomeNode = strict ? new bool[typeCount][] : null;

        for (var typeIndex = 0; typeIndex < typeCount; typeIndex++)
        {
            var hw = accessor.HighWater(typeIndex);
            expectedRefCount[typeIndex] = new int[hw];
            color[typeIndex] = new int[hw];
#pragma warning disable IDE0031 // Roslyn suggests null-conditional assignment, which requires preview lang version
            if (isChildOfSomeNode != null)
                isChildOfSomeNode[typeIndex] = new bool[hw];
#pragma warning restore IDE0031
        }

        // Count root holds
        for (var i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            if (!IsInRange(root, expectedRefCount))
            {
                issues.Add($"Root (type={root.TypeId}, index={root.Index}) is out of range.");
                continue;
            }

            expectedRefCount[root.TypeId][root.Index]++;
        }

        // DFS from each root
        var childBuffer = new List<NodeRef>();
        var dfsStack = new Stack<(NodeRef node, bool entering)>();

        for (var i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            if (!IsInRange(root, expectedRefCount))
                continue;
            if (color[root.TypeId][root.Index] != 0)
                continue;

            dfsStack.Push((root, true));

            while (dfsStack.Count > 0)
            {
                var (node, entering) = dfsStack.Pop();

                if (!entering)
                {
                    color[node.TypeId][node.Index] = 2; // black
                    continue;
                }

                if (color[node.TypeId][node.Index] == 2)
                    continue;

                if (color[node.TypeId][node.Index] == 1)
                {
                    issues.Add($"Cycle detected: node (type={node.TypeId}, index={node.Index}) is part of a cycle.");
                    continue;
                }

                color[node.TypeId][node.Index] = 1; // gray
                dfsStack.Push((node, false)); // post-visit marker

                childBuffer.Clear();
                accessor.GetChildren(node.TypeId, node.Index, childBuffer);

                for (var childIndex = 0; childIndex < childBuffer.Count; childIndex++)
                {
                    var child = childBuffer[childIndex];
                    if (!IsInRange(child, expectedRefCount))
                    {
                        issues.Add($"Node (type={node.TypeId}, index={node.Index}) has out-of-range child (type={child.TypeId}, index={child.Index}).");
                        continue;
                    }

#pragma warning disable IDE0031
                    if (isChildOfSomeNode != null)
                        isChildOfSomeNode[child.TypeId][child.Index] = true;
#pragma warning restore IDE0031

                    expectedRefCount[child.TypeId][child.Index]++;

                    if (color[child.TypeId][child.Index] == 1)
                    {
                        issues.Add($"Cycle detected: edge (type={node.TypeId}, index={node.Index}) → (type={child.TypeId}, index={child.Index}) forms a back-edge.");
                    }
                    else if (color[child.TypeId][child.Index] == 0)
                    {
                        dfsStack.Push((child, true));
                    }
                }
            }
        }

        // Compare expected vs actual refcounts
        for (var typeIndex = 0; typeIndex < typeCount; typeIndex++)
        {
            for (var i = 0; i < expectedRefCount[typeIndex].Length; i++)
            {
                var actual = accessor.GetRefCount(typeIndex, i);
                var expected = expectedRefCount[typeIndex][i];
                var reachable = color[typeIndex][i] == 2;

                if (strict)
                {
                    if (actual != expected)
                        issues.Add(
                            $"Node (type={typeIndex}, index={i}): expected refcount {expected}, actual {actual} (reachable={reachable}).");
                }
                else
                {
                    if (reachable && actual < expected)
                        issues.Add(
                            $"Node (type={typeIndex}, index={i}): refcount too low — expected at least {expected}, actual {actual}.");
                }
            }
        }

        // Root purity (strict only)
        if (isChildOfSomeNode != null)
        {
            for (var i = 0; i < roots.Length; i++)
            {
                var root = roots[i];
                if (IsInRange(root, expectedRefCount) && isChildOfSomeNode[root.TypeId][root.Index])
                    issues.Add($"Root (type={root.TypeId}, index={root.Index}) is also a child of another node in the DAG.");
            }
        }

        return issues;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsInRange(NodeRef node, int[][] arrays)
        => node.TypeId >= 0 && node.TypeId < arrays.Length
           && node.Index >= 0 && node.Index < arrays[node.TypeId].Length;

    // ── Single-store convenience ────────────────────────────────────────

    /// <summary>
    /// Validates a single store's DAG using an <see cref="INodeOps{TNode}"/>.
    /// Cross-type children produced by the enumerator are ignored — use the cross-store overload
    /// for full heterogeneous validation.
    /// </summary>
    internal static void AssertValid<TNode, TNodeOps>(
        GlobalNodeStore<TNode, TNodeOps> store,
        ReadOnlySpan<Handle<TNode>> roots,
        TNodeOps enumerator,
        bool strict = true)
        where TNode : struct
        where TNodeOps : struct, INodeOps<TNode>
    {
        var issues = Validate(store, roots, enumerator, strict);
        if (issues.Count > 0)
            throw new DagValidationException(issues);
    }

    /// <summary>Single-store validate returning issues list.</summary>
    internal static List<string> Validate<TNode, TNodeOps>(
        GlobalNodeStore<TNode, TNodeOps> store,
        ReadOnlySpan<Handle<TNode>> roots,
        TNodeOps enumerator,
        bool strict = true)
        where TNode : struct
        where TNodeOps : struct, INodeOps<TNode>
    {
        var accessor = new SingleStoreAccessor<TNode, TNodeOps>(store, enumerator);
        var nodeRefs = roots.Length <= 128 ? stackalloc NodeRef[roots.Length] : new NodeRef[roots.Length];
        for (var i = 0; i < roots.Length; i++)
            nodeRefs[i] = new NodeRef(0, roots[i].Index);

        return Validate(nodeRefs, accessor, strict);
    }

    // ── SingleStoreAccessor ─────────────────────────────────────────────

    /// <summary>
    /// Adapts a single <see cref="GlobalNodeStore{TNode,TNodeOps}"/> + <see cref="INodeOps{TNode}"/>
    /// into an <see cref="IWorldAccessor"/> with one type (typeId = 0). Cross-type children from the
    /// enumerator are silently filtered out.
    /// </summary>
    private struct SingleStoreAccessor<TNode, TNodeOps>(
        GlobalNodeStore<TNode, TNodeOps> store,
        TNodeOps enumerator) : IWorldAccessor
        where TNode : struct
        where TNodeOps : struct, INodeOps<TNode>
    {
        public readonly int TypeCount => 1;

        public readonly int HighWater(int typeId) => store.Arena.HighWater;

        public readonly int GetRefCount(int typeId, int index) => store.RefCounts.GetCount(new Handle<TNode>(index));

        public void GetChildren(int typeId, int index, List<NodeRef> children)
        {
            ref readonly var node = ref store.Arena[new Handle<TNode>(index)];
            var visitor = new CollectSameTypeNodeVisitor<TNode>(children);
            enumerator.EnumerateChildren(in node, ref visitor);
        }
    }

    /// <summary>
    /// <see cref="INodeVisitor"/> that collects only same-type children as <see cref="NodeRef"/> values
    /// with typeId 0. Cross-type children are silently ignored. The <c>typeof(T) == typeof(TNode)</c>
    /// check is a JIT constant — the dead branch is eliminated entirely.
    /// </summary>
    private struct CollectSameTypeNodeVisitor<TNode>(List<NodeRef> children) : INodeVisitor where TNode : struct
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Visit<T>(Handle<T> handle) where T : struct
        {
            if (typeof(T) == typeof(TNode))
                this.CollectTyped(Unsafe.As<Handle<T>, Handle<TNode>>(ref handle));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly void CollectTyped(Handle<TNode> child)
        {
            Debug.Assert(child.IsValid);
            children.Add(new NodeRef(0, child.Index));
        }
    }
}

/// <summary>Thrown by <see cref="DagValidator.AssertValid{TAccessor}"/> when one or more
/// DAG invariants are violated.</summary>
internal sealed class DagValidationException(List<string> issues) : Exception(FormatMessage(issues))
{
    public IReadOnlyList<string> Issues { get; } = issues;

    private static string FormatMessage(List<string> issues)
        => $"DAG validation failed with {issues.Count} issue(s):\n" +
           string.Join("\n", issues.Select((issue, i) => $"  [{i + 1}] {issue}"));
}
