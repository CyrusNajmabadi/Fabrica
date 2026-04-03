namespace Fabrica.Core.Memory;

/// <summary>
/// Validates structural invariants of arena-backed DAGs. Intended for use in tests and debug scenarios —
/// validation is O(N) in the number of live nodes and should not be called on hot paths.
///
/// TWO MODES
///   <b>Strict</b> (default): checks exact refcount match, reachability, root purity, empty-roots.
///   Use when the caller knows ALL roots are registered and there are no cross-store references.
///
///   <b>Relaxed</b>: checks <c>actual &gt;= expected</c> instead of exact match. Tolerates extra refcounts
///   from cross-store references or roots not yet tracked. Used by automatic validation in
///   <see cref="NodeStore{TNode, THandler}.EnableValidation{TEnumerator}"/>.
///
/// INVARIANTS CHECKED (both modes)
///   1. Acyclicity — the graph is a DAG, not a cyclic graph.
///   2. Refcount floor — every reachable node's stored refcount is at least the computed count.
///   3. No dangling references — children of reachable nodes are within the valid index range.
///
/// ADDITIONAL INVARIANTS (strict mode only)
///   4. Refcount accuracy — every node's stored refcount exactly matches the computed count.
///   5. Reachability — every node with refcount &gt; 0 is reachable from at least one root.
///   6. Root purity — no root is a child of another reachable node (true roots have no in-edges).
///
/// USAGE
///   Define a struct implementing <see cref="IChildEnumerator{TNode}"/> that appends same-store child
///   handles to the provided list. Then call <see cref="AssertValid"/> or <see cref="Validate"/>
///   after building or modifying a DAG.
///
/// PORTABILITY
///   Pure computation over arrays/indices. No GC-specific features.
/// </summary>
internal static class DagValidator
{
    /// <summary>
    /// Struct callback that enumerates the same-store child handles of a node. Implementations should
    /// append only valid child handles to <paramref name="children"/>. Handles where
    /// <see cref="Handle{T}.IsValid"/> is false must be filtered out by the implementation.
    /// </summary>
    internal interface IChildEnumerator<TNode> where TNode : struct
    {
        void GetChildren(in TNode node, List<Handle<TNode>> children);
    }

    /// <summary>
    /// Validates DAG invariants and throws <see cref="DagValidationException"/> if any are violated.
    /// </summary>
    /// <param name="strict">When true (default), checks exact refcount match and reachability.
    /// When false, only checks <c>actual &gt;= expected</c> (tolerates cross-store or untracked references).</param>
    internal static void AssertValid<TNode, THandler, TEnumerator>(
        NodeStore<TNode, THandler> store,
        ReadOnlySpan<Handle<TNode>> roots,
        TEnumerator enumerator,
        bool strict = true)
        where TNode : struct
        where THandler : struct, RefCountTable<TNode>.IRefCountHandler
        where TEnumerator : struct, IChildEnumerator<TNode>
    {
        var issues = Validate(store, roots, enumerator, strict);
        if (issues.Count > 0)
            throw new DagValidationException(issues);
    }

    /// <summary>
    /// Validates DAG invariants and returns a list of violation descriptions. An empty list means
    /// the DAG is well-formed (within the chosen strictness level).
    /// </summary>
    /// <param name="strict">When true (default), checks exact refcount match and reachability.
    /// When false, only checks <c>actual &gt;= expected</c>.</param>
    internal static List<string> Validate<TNode, THandler, TEnumerator>(
        NodeStore<TNode, THandler> store,
        ReadOnlySpan<Handle<TNode>> roots,
        TEnumerator enumerator,
        bool strict = true)
        where TNode : struct
        where THandler : struct, RefCountTable<TNode>.IRefCountHandler
        where TEnumerator : struct, IChildEnumerator<TNode>
    {
        var issues = new List<string>();
        var highWater = store.Arena.GetTestAccessor().HighWater;

        // expectedRefCount[i] = (number of parent edges pointing at i among reachable nodes) + (number of root holds on i)
        var expectedRefCount = new int[highWater];

        // DFS coloring: 0 = white (unvisited), 1 = gray (in current path), 2 = black (fully visited)
        var color = new int[highWater];

        // Track which indices appear as children of any reachable node (for root purity check)
        var isChildOfSomeNode = strict ? new bool[highWater] : null;

        // Reusable buffer for child enumeration
        var childBuffer = new List<Handle<TNode>>();

        // Count root holds
        for (var i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            if (root.Index < 0 || root.Index >= highWater)
            {
                issues.Add($"Root index {root.Index} is out of range [0, {highWater}).");
                continue;
            }

            expectedRefCount[root.Index]++;
        }

        // DFS from each root
        var dfsStack = new Stack<(int index, bool entering)>();
        for (var i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            if (root.Index < 0 || root.Index >= highWater)
                continue;
            if (color[root.Index] != 0)
                continue;

            dfsStack.Push((root.Index, true));

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
                enumerator.GetChildren(in store.Arena[new Handle<TNode>(index)], childBuffer);

                for (var c = 0; c < childBuffer.Count; c++)
                {
                    var child = childBuffer[c];
                    if (child.Index < 0 || child.Index >= highWater)
                    {
                        issues.Add($"Node {index} has out-of-range child index {child.Index}.");
                        continue;
                    }

                    isChildOfSomeNode?[child.Index] = true;

                    expectedRefCount[child.Index]++;

                    if (color[child.Index] == 1)
                    {
                        issues.Add($"Cycle detected: edge {index} → {child.Index} forms a back-edge.");
                    }
                    else if (color[child.Index] == 0)
                    {
                        dfsStack.Push((child.Index, true));
                    }
                }
            }
        }

        // Compare expected vs actual refcounts
        for (var i = 0; i < highWater; i++)
        {
            var actual = store.RefCounts.GetCount(new Handle<TNode>(i));
            var expected = expectedRefCount[i];
            var reachable = color[i] == 2;

            if (strict)
            {
                if (actual != expected)
                    issues.Add(
                        $"Node {i}: expected refcount {expected}, actual {actual}" +
                        $" (reachable={reachable}).");
            }
            else
            {
                // Relaxed: actual must be at least expected. Extra refcounts are OK (cross-store
                // references or untracked roots contribute additional holds).
                if (reachable && actual < expected)
                    issues.Add(
                        $"Node {i}: refcount too low — expected at least {expected}, actual {actual}.");
            }
        }

        // Root purity (strict mode only)
        if (isChildOfSomeNode != null)
        {
            for (var i = 0; i < roots.Length; i++)
            {
                var root = roots[i];
                if (root.Index >= 0 && root.Index < highWater && isChildOfSomeNode[root.Index])
                    issues.Add($"Root {root.Index} is also a child of another node in the DAG.");
            }
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
