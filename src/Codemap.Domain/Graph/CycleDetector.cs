namespace Codemap.Domain.Graph;

/// <summary>
/// Cycle detection over the edge list via iterative Tarjan SCC (a DFS). V1 reports the count only:
/// each strongly connected component with more than one node, plus each self-loop, counts as one cycle.
/// </summary>
public static class CycleDetector
{
    public static int CountCycles(IReadOnlyList<CodeEdge> edges)
    {
        var adjacency = BuildAdjacency(edges, out var selfLoops);
        var count = selfLoops;

        var index = 0;
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();

        foreach (var start in adjacency.Keys)
        {
            if (indices.ContainsKey(start)) continue;

            // Iterative Tarjan to survive deep graphs without stack overflow.
            var work = new Stack<(string Node, int EdgeIndex)>();
            work.Push((start, 0));

            while (work.Count > 0)
            {
                var (node, edgeIndex) = work.Pop();
                if (edgeIndex == 0)
                {
                    indices[node] = index;
                    lowLinks[node] = index;
                    index++;
                    stack.Push(node);
                    onStack.Add(node);
                }

                var neighbors = adjacency[node];
                var advanced = false;
                for (var i = edgeIndex; i < neighbors.Count; i++)
                {
                    var next = neighbors[i];
                    if (!indices.ContainsKey(next))
                    {
                        work.Push((node, i + 1));
                        work.Push((next, 0));
                        advanced = true;
                        break;
                    }
                    if (onStack.Contains(next))
                        lowLinks[node] = Math.Min(lowLinks[node], indices[next]);
                }
                if (advanced) continue;

                if (lowLinks[node] == indices[node])
                {
                    var size = 0;
                    string popped;
                    do
                    {
                        popped = stack.Pop();
                        onStack.Remove(popped);
                        size++;
                    } while (!popped.Equals(node, StringComparison.Ordinal));
                    if (size > 1) count++;
                }

                if (work.Count > 0)
                {
                    var (parent, _) = work.Peek();
                    lowLinks[parent] = Math.Min(lowLinks[parent], lowLinks[node]);
                }
            }
        }

        return count;
    }

    private static Dictionary<string, List<string>> BuildAdjacency(IReadOnlyList<CodeEdge> edges, out int selfLoops)
    {
        selfLoops = 0;
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var seenSelfLoops = new HashSet<string>(StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            if (edge.FromId.Equals(edge.ToId, StringComparison.Ordinal))
            {
                if (seenSelfLoops.Add(edge.FromId)) selfLoops++;
                continue;
            }
            if (!adjacency.TryGetValue(edge.FromId, out var list))
                adjacency[edge.FromId] = list = [];
            list.Add(edge.ToId);
            if (!adjacency.ContainsKey(edge.ToId))
                adjacency[edge.ToId] = [];
        }
        return adjacency;
    }
}
