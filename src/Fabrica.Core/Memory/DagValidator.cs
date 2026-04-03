namespace Fabrica.Core.Memory;

/// <summary>
/// Validates structural invariants of arena-backed DAGs. Intended for use in tests and debug scenarios —
/// validation is O(N) in the number of live nodes and should not be called on hot paths.
///
/// INVARIANTS CHECKED
///   1. Acyclicity — the graph is a DAG, not a cyclic graph.
///   2. Refcount accuracy — every node's stored refcount matches the computed count (parent edges + root holds).
///   3. Reachability — every node with refcount > 0 is reachable from at least one root.
///   4. Root purity — no root is a child of another reachable node (true roots have no in-edges).
///   5. Empty roots — if the root set is empty, no node should have a positive refcount.
///   6. No dangling references — children of reachable nodes have positive refcounts.
///
/// USAGE
///   Define a struct implementing <see cref="IChildEnumerator{TNode}"/> that appends same-store child
///   indices (>= 0) to the provided list. Then call <see cref="AssertValid"/> or <see cref="Validate"/>
///   after building or modifying a DAG.
///
/// PORTABILITY
///   Pure computation over arrays/indices. No GC-specific features.
/// </summary>
internal static class DagValidator
{
    /// <summary>
    /// Struct callback that enumerates the same-store child indices of a node. Implementations should
    /// append only valid (>= 0) child indices to <paramref name="children"/>. Sentinel values (-1) must
    /// be filtered out by the implementation.
    /// </summary>
    internal interface IChildEnumerator<TNode> where TNode : struct
    {
        void GetChildren(in TNode node, List<int> children);
    }

    /// <summary>
    /// Validates all DAG invariants and throws <see cref="DagValidationException"/> if any are violated.
    /// The exception message contains all violations found.
    /// </summary>
    internal static void AssertValid<TNode, THandler, TEnumerator>(
        NodeStore<TNode, THandler> store,
        ReadOnlySpan<int> roots,
        TEnumerator enumerator)
        where TNode : struct
        where THandler : struct, RefCountTable.IRefCountHandler
        where TEnumerator : struct, IChildEnumerator<TNode>
    {
        var issues = Validate(store, roots, enumerator);
        if (issues.Count > 0)
            throw new DagValidationException(issues);
    }

    /// <summary>
    /// Validates all DAG invariants and returns a list of violation descriptions. An empty list means
    /// the DAG is well-formed.
    /// </summary>
    internal static List<string> Validate<TNode, THandler, TEnumerator>(
        NodeStore<TNode, THandler> store,
        ReadOnlySpan<int> roots,
        TEnumerator enumerator)
        where TNode : struct
        where THandler : struct, RefCountTable.IRefCountHandler
        where TEnumerator : struct, IChildEnumerator<TNode>
    {
        var issues = new List<string>();
        var highWater = store.Arena.GetTestAccessor().HighWater;

        // expectedRefCount[i] = (number of parent edges pointing at i among reachable nodes) + (number of root holds on i)
        var expectedRefCount = new int[highWater];

        // DFS coloring: 0 = white (unvisited), 1 = gray (in current path), 2 = black (fully visited)
        var color = new int[highWater];

        // Track which indices appear as children of any reachable node (for root purity check)
        var isChildOfSomeNode = new bool[highWater];

        // Reusable buffer for child enumeration
        var childBuffer = new List<int>();

        // Count root holds
        for (var i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            if (root < 0 || root >= highWater)
            {
                issues.Add($"Root index {root} is out of range [0, {highWater}).");
                continue;
            }

            expectedRefCount[root]++;
        }

        // DFS from each root
        var dfsStack = new Stack<(int index, bool entering)>();
        for (var i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            if (root < 0 || root >= highWater)
                continue;
            if (color[root] != 0)
                continue;

            dfsStack.Push((root, true));

            while (dfsStack.Count > 0)
            {
                var (index, entering) = dfsStack.Pop();

                if (!entering)
                {
                    color[index] = 2; // black
                    continue;
                }

                if (color[index] == 2)
                    continue;

                if (color[index] == 1)
                {
                    issues.Add($"Cycle detected: node {index} is part of a cycle.");
                    continue;
                }

                color[index] = 1; // gray
                dfsStack.Push((index, false)); // post-visit marker

                childBuffer.Clear();
                enumerator.GetChildren(in store.Arena[index], childBuffer);

                for (var c = 0; c < childBuffer.Count; c++)
                {
                    var child = childBuffer[c];
                    if (child < 0 || child >= highWater)
                    {
                        issues.Add($"Node {index} has out-of-range child index {child}.");
                        continue;
                    }

                    isChildOfSomeNode[child] = true;
                    expectedRefCount[child]++;

                    if (color[child] == 1)
                    {
                        issues.Add($"Cycle detected: edge {index} → {child} forms a back-edge.");
                    }
                    else if (color[child] == 0)
                    {
                        dfsStack.Push((child, true));
                    }
                }
            }
        }

        // Compare expected vs actual refcounts for all indices in range
        for (var i = 0; i < highWater; i++)
        {
            var actual = store.RefCounts.GetCount(i);
            var expected = expectedRefCount[i];

            if (actual != expected)
            {
                var reachable = color[i] == 2;
                issues.Add(
                    $"Node {i}: expected refcount {expected}, actual {actual}" +
                    $" (reachable={reachable}).");
            }
        }

        // Root purity: roots should not be children of other reachable nodes
        for (var i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            if (root >= 0 && root < highWater && isChildOfSomeNode[root])
                issues.Add($"Root {root} is also a child of another node in the DAG.");
        }

        return issues;
    }
}

/// <summary>Thrown by <see cref="DagValidator.AssertValid{TNode,THandler,TEnumerator}"/> when one or more
/// DAG invariants are violated.</summary>
internal sealed class DagValidationException(List<string> issues) : Exception(FormatMessage(issues))
{
    public IReadOnlyList<string> Issues { get; } = issues;

    private static string FormatMessage(List<string> issues)
        => $"DAG validation failed with {issues.Count} issue(s):\n" +
           string.Join("\n", issues.Select((issue, i) => $"  [{i + 1}] {issue}"));
}
