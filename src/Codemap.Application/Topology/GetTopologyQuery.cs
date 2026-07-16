using Codemap.Application.Abstractions;
using Codemap.Application.Messaging;
using Codemap.Domain;

namespace Codemap.Application.Topology;

/// <summary>
/// Returns the full node/edge graph for a scan result or published snapshot.
/// <paramref name="NamespaceScope"/> scopes the result to nodes whose namespace starts with the given
/// prefix; <paramref name="MaxNodes"/> caps the payload for very large graphs (spec: >1000 nodes must
/// not be shipped to the client wholesale).
/// </summary>
public sealed record GetTopologyQuery(Guid GraphId, string? NamespaceScope = null, int MaxNodes = 1000)
    : IRequest<TopologyGraph?>;

public sealed class GetTopologyQueryHandler(IScanResultStore store, IGraphRepository repository)
    : IRequestHandler<GetTopologyQuery, TopologyGraph?>
{
    public async Task<TopologyGraph?> Handle(GetTopologyQuery request, CancellationToken ct = default)
    {
        // Fresh (unpublished) scans live in the in-memory store; published snapshots in SQL Server.
        var graph = store.Get(request.GraphId)
                    ?? await repository.GetAsync(request.GraphId, ct).ConfigureAwait(false);
        if (graph is null) return null;

        var nodes = graph.Nodes;
        if (!string.IsNullOrWhiteSpace(request.NamespaceScope))
        {
            // Segment-aware prefix: "App.Core" matches "App.Core" and "App.Core.X" ('.' for C#,
            // '/' for JS module paths) but never "App.CoreExtras".
            var scope = request.NamespaceScope;
            nodes = nodes
                .Where(n => n.Namespace.Equals(scope, StringComparison.OrdinalIgnoreCase)
                            || n.Namespace.StartsWith(scope + ".", StringComparison.OrdinalIgnoreCase)
                            || n.Namespace.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (nodes.Count > request.MaxNodes)
        {
            // Deterministic cap: keep the alphabetically-first namespaces intact rather than a random slice,
            // so the scoped view stays coherent. Callers drill in via NamespaceScope for the rest.
            nodes = nodes
                .OrderBy(n => n.Namespace, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Take(request.MaxNodes)
                .ToList();
        }

        if (nodes.Count == graph.Nodes.Count) return graph;

        var visible = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var edges = graph.Edges.Where(e => visible.Contains(e.FromId) && visible.Contains(e.ToId)).ToList();
        return graph with { Nodes = nodes, Edges = edges };
    }
}
