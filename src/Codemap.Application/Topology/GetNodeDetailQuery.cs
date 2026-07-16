using Codemap.Application.Abstractions;
using Codemap.Application.Messaging;
using Codemap.Domain;

namespace Codemap.Application.Topology;

/// <summary>
/// Inspector-panel detail: a single node plus its inbound/outbound edges (with the node on the far
/// end of each edge, when it exists in the graph). Assumption: the spec's signature carries only
/// NodeId, but node ids are unique per graph, not globally — the query needs the graph id too.
/// </summary>
public sealed record GetNodeDetailQuery(Guid GraphId, string NodeId) : IRequest<NodeDetail?>;

public sealed record NodeRelation(CodeEdge Edge, CodeNode? Other);

public sealed record NodeDetail(
    CodeNode Node,
    IReadOnlyList<NodeRelation> DependsOn,
    IReadOnlyList<NodeRelation> DependedOnBy);

public sealed class GetNodeDetailQueryHandler(IScanResultStore store, IGraphRepository repository)
    : IRequestHandler<GetNodeDetailQuery, NodeDetail?>
{
    public async Task<NodeDetail?> Handle(GetNodeDetailQuery request, CancellationToken ct = default)
    {
        var graph = store.Get(request.GraphId)
                    ?? await repository.GetAsync(request.GraphId, ct).ConfigureAwait(false);
        var node = graph?.Nodes.FirstOrDefault(n => n.Id.Equals(request.NodeId, StringComparison.Ordinal));
        if (graph is null || node is null) return null;

        var byId = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        var outbound = graph.Edges
            .Where(e => e.FromId.Equals(request.NodeId, StringComparison.Ordinal))
            .Select(e => new NodeRelation(e, byId.GetValueOrDefault(e.ToId)))
            .ToList();

        var inbound = graph.Edges
            .Where(e => e.ToId.Equals(request.NodeId, StringComparison.Ordinal))
            .Select(e => new NodeRelation(e, byId.GetValueOrDefault(e.FromId)))
            .ToList();

        return new NodeDetail(node, outbound, inbound);
    }
}
