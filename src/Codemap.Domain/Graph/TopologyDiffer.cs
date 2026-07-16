namespace Codemap.Domain.Graph;

/// <summary>
/// Pure in-memory diff between two topology snapshots. Nodes are matched by <see cref="CodeNode.Id"/>.
/// Assumption: a node counts as "changed" when its structural shape differs (kind, language, namespace,
/// display name, or member signatures). SourceFile/LineNumber-only changes are ignored as noise —
/// moving a class a few lines down is not a topology change.
/// </summary>
public static class TopologyDiffer
{
    public static TopologyDiff Diff(TopologyGraph from, TopologyGraph to)
    {
        var fromNodes = from.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var toNodes = to.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        var added = to.Nodes.Where(n => !fromNodes.ContainsKey(n.Id)).ToList();
        var removed = from.Nodes.Where(n => !toNodes.ContainsKey(n.Id)).ToList();

        var changed = new List<(CodeNode Before, CodeNode After)>();
        foreach (var after in to.Nodes)
        {
            if (fromNodes.TryGetValue(after.Id, out var before) && !StructurallyEqual(before, after))
                changed.Add((before, after));
        }

        var fromEdges = new HashSet<CodeEdge>(from.Edges);
        var toEdges = new HashSet<CodeEdge>(to.Edges);
        var edgesAdded = to.Edges.Where(e => !fromEdges.Contains(e)).Distinct().ToList();
        var edgesRemoved = from.Edges.Where(e => !toEdges.Contains(e)).Distinct().ToList();

        return new TopologyDiff(added, removed, changed, edgesAdded, edgesRemoved);
    }

    private static bool StructurallyEqual(CodeNode a, CodeNode b) =>
        a.DisplayName == b.DisplayName
        && a.Kind == b.Kind
        && a.Language == b.Language
        && a.Namespace == b.Namespace
        && a.Members.SequenceEqual(b.Members);
}
